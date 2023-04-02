using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Gui;
using Dalamud.Game.Text;
using Dalamud.Logging;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using LMeter.Config;
using LMeter.Helpers;
using Newtonsoft.Json;

namespace LMeter.ACT
{
    public class IINACTClient : IACTClient
    {
        private readonly ACTConfig _config;
        private readonly DalamudPluginInterface _dpi;
        private ACTEvent? _lastEvent;
        private readonly ICallGateProvider<string, bool> _combatEventReaderIpc;

        public ConnectionStatus Status { get; private set; }
        public List<ACTEvent> PastEvents { get; private set; }

        public IINACTClient(ACTConfig config, DalamudPluginInterface dpi)
        {
            _config = config;
            _dpi = dpi;
            Status = ConnectionStatus.NotConnected;
            PastEvents = new List<ACTEvent>();

            _combatEventReaderIpc = _dpi.GetIpcProvider<string, bool>("LMeter.CombatEventReader");
            _combatEventReaderIpc.RegisterFunc(ReceiveIpcMessage);
        }

        public ACTEvent? GetEvent(int index = -1)
        {
            if (index >= 0 && index < PastEvents.Count)
            {
                return PastEvents[index];
            }
            
            return _lastEvent;
        }

        public void EndEncounter()
        {
            ChatGui chat = Singletons.Get<ChatGui>();
            XivChatEntry message = new XivChatEntry()
            {
                Message = "end",
                Type = XivChatType.Echo
            };

            chat.PrintChat(message);
        }

        public void Clear()
        {
            _lastEvent = null;
            PastEvents = new List<ACTEvent>();
            if (_config.ClearACT)
            {
                ChatGui chat = Singletons.Get<ChatGui>();
                XivChatEntry message = new XivChatEntry()
                {
                    Message = "clear",
                    Type = XivChatType.Echo
                };

                chat.PrintChat(message);
            }
        }

        public void RetryConnection(string address)
        {
            Reset();
            Start();
        }

        public void Start()
        {
            if (Status != ConnectionStatus.NotConnected)
            {
                PluginLog.Error("Cannot start, ACTClient needs to be reset!");
                return;
            }

            var connectSuccess = Connect();
            if (!connectSuccess)
            {
                Status = ConnectionStatus.ConnectionFailed;
                return;
            }

            Status = ConnectionStatus.Connected;
            PluginLog.Information("Successfully Established ACT Connection");
        }

        private bool Connect()
        {
            try
            {
                Status = ConnectionStatus.Connecting;
                return _dpi
                    .GetIpcSubscriber<string, bool>("IINACT.Server.SubscribeToCombatEvents")
                    .InvokeFunc("LMeter.CombatEventReader");
            }
            catch (Exception ex)
            {
                Status = ConnectionStatus.ConnectionFailed;
                this.LogConnectionFailure(ex.ToString());
                return false;
            }
        }

        private bool ReceiveIpcMessage(string data)
        {
            if (string.IsNullOrEmpty(data)) return false;
            try
            {
                ACTEvent? newEvent = JsonConvert.DeserializeObject<ACTEvent>(data);

                if (newEvent?.Encounter is not null &&
                    newEvent?.Combatants is not null &&
                    newEvent.Combatants.Any() &&
                    (CharacterState.IsInCombat() || !newEvent.IsEncounterActive()))
                {
                    if (!(_lastEvent is not null &&
                          _lastEvent.IsEncounterActive() == newEvent.IsEncounterActive() &&
                          _lastEvent.Encounter is not null &&
                          _lastEvent.Encounter.Duration.Equals(newEvent.Encounter.Duration)))
                    {
                        if (!newEvent.IsEncounterActive())
                        {
                            PastEvents.Add(newEvent);

                            while (PastEvents.Count > _config.EncounterHistorySize)
                            {
                                PastEvents.RemoveAt(0);
                            }
                        }

                        newEvent.Timestamp = DateTime.UtcNow;
                        _lastEvent = newEvent;
                    }
                }
            }
            catch (Exception ex)
            {
                this.LogConnectionFailure(ex.ToString());
                return false;
            }

            return true;
        }
        
        public void Shutdown()
        {
            try
            {
                _dpi
                    .GetIpcSubscriber<string, bool>("IINACT.Server.UnsubscribeFromCombatEvents")
                    .InvokeFunc("LMeter.CombatEventReader");
            }
            catch (Exception)
            {
                // don't throw when closing
            }

            Status = ConnectionStatus.NotConnected;
        }

        public void Reset()
        {
            this.Shutdown();
            Status = ConnectionStatus.NotConnected;
        }

        private void LogConnectionFailure(string error)
        {
            PluginLog.Debug($"Failed to connect to ACT!");
            PluginLog.Verbose(error);
        }

        public void Dispose()
        {
            _combatEventReaderIpc.UnregisterFunc();
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.Shutdown();
            }
        }
    }
}
