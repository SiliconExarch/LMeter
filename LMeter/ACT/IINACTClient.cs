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
using Newtonsoft.Json.Linq;

namespace LMeter.ACT
{
    public class IINACTClient : IACTClient
    {
        private readonly ACTConfig _config;
        private readonly DalamudPluginInterface _dpi;
        private ACTEvent? _lastEvent;
        private readonly ICallGateProvider<JObject, bool> _combatEventReaderIpc;

        private const string LMeterSubscriptionIpcEndpoint = "LMeter.SubscriptionReceiver";
        private const string IINACTSubscribeIpcEndpoint = "IINACT.CreateLegacySubscriber";
        private const string IINACTUnsubscribeIpcEndpoint = "IINACT.Unsubscribe";

        public ConnectionStatus Status { get; private set; }
        public List<ACTEvent> PastEvents { get; private set; }

        public IINACTClient(ACTConfig config, DalamudPluginInterface dpi)
        {
            _config = config;
            _dpi = dpi;
            Status = ConnectionStatus.NotConnected;
            PastEvents = new List<ACTEvent>();

            _combatEventReaderIpc = _dpi.GetIpcProvider<JObject, bool>(LMeterSubscriptionIpcEndpoint);
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

        public void RetryConnection()
        {
            Reset();
            Start();
        }

        public void Start()
        {
            if (Status != ConnectionStatus.NotConnected)
            {
                PluginLog.Error("Cannot start, IINACTClient needs to be reset!");
                return;
            }

            var connectSuccess = Connect();
            if (!connectSuccess)
            {
                Status = ConnectionStatus.ConnectionFailed;
                return;
            }

            Status = ConnectionStatus.Connected;
            PluginLog.Information("Successfully subscribed to IINACT");
        }

        private bool Connect()
        {
            try
            {
                Status = ConnectionStatus.Connecting;
                return _dpi
                    .GetIpcSubscriber<string, bool>(IINACTSubscribeIpcEndpoint)
                    .InvokeFunc(LMeterSubscriptionIpcEndpoint);
            }
            catch (Exception ex)
            {
                Status = ConnectionStatus.ConnectionFailed;
                PluginLog.Debug("Failed to subscribe to IINACT!");
                PluginLog.Verbose(ex.ToString());
                return false;
            }
        }

        private bool ReceiveIpcMessage(JObject? data)
        {
            // `is` statements do auto null checking \o/
            if (data?["msgtype"]?.ToString() is not "CombatData") return false;

            try
            {
                ACTEvent? newEvent = data?["msg"]?.ToObject<ACTEvent?>();

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
                PluginLog.Verbose(ex.ToString());
                return false;
            }

            return true;
        }
        
        public void Shutdown()
        {
            try
            {
                var success = _dpi
                    .GetIpcSubscriber<string, bool>(IINACTUnsubscribeIpcEndpoint)
                    .InvokeFunc(LMeterSubscriptionIpcEndpoint);

                PluginLog.Information(
                    success
                        ? "Successfully unsubscribed from IINACT"
                        : "Failed to unsubscribe from IINACT"
                );
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
