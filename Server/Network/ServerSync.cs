﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows;
using Caliburn.Micro;
using Ciribob.SRS.Common;
using Ciribob.SRS.Common.Network;
using Ciribob.SRS.Common.Setting;
using Ciribob.DCS.SimpleRadio.Standalone.Server.Settings;
using NetCoreServer;
using Newtonsoft.Json;
using NLog;
using Open.Nat;
using LogManager = NLog.LogManager;

namespace Ciribob.SRS.Server.Network
{
    public class ServerSync : TcpServer, IHandle<ServerSettingsChangedMessage>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly HashSet<IPAddress> _bannedIps;

        private readonly ConcurrentDictionary<string, SRClient> _clients = new ConcurrentDictionary<string, SRClient>();
        private readonly IEventAggregator _eventAggregator;

        private readonly ServerSettingsStore _serverSettings;


        public ServerSync(ConcurrentDictionary<string, SRClient> connectedClients, HashSet<IPAddress> _bannedIps,
            IEventAggregator eventAggregator) : base(IPAddress.Any, ServerSettingsStore.Instance.GetServerPort())
        {
            _clients = connectedClients;
            this._bannedIps = _bannedIps;
            _eventAggregator = eventAggregator;
            _eventAggregator.Subscribe(this);
            _serverSettings = ServerSettingsStore.Instance;

            OptionKeepAlive = true;

            
        }

        public void Handle(ServerSettingsChangedMessage message)
        {
            try
            {
                HandleServerSettingsMessage();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Exception Sending Server Settings ");
            }
        }

        protected override TcpSession CreateSession() { return new SRSClientSession(this, _clients, _bannedIps); }

        protected override void OnError(SocketError error)
        {
            Logger.Error($"TCP SERVER ERROR: {error} ");
        }

        public void StartListening()
        {
            OptionKeepAlive = true;
            try
            {
                Start();
            }
            catch(Exception ex)
            {
                
                Logger.Error(ex,$"Unable to start the SRS Server");

                MessageBox.Show($"Unable to start the SRS Server\n\nPort {_serverSettings.GetServerPort()} in use\n\nChange the port by editing the .cfg", "Port in use",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(0);
            }
        }

        public void HandleDisconnect(SRSClientSession state)
        {
            Logger.Info("Disconnecting Client");

            if ((state != null) && (state.SRSGuid != null))
            {

                //removed
                SRClient client;
                _clients.TryRemove(state.SRSGuid, out client);

                if (client != null)
                {
                    Logger.Info("Removed Disconnected Client " + state.SRSGuid);
                    client.ClientSession = null;

                   
                    HandleClientDisconnect(state, client);
                }

                try
                {
                    _eventAggregator.PublishOnUIThread(
                        new ServerStateMessage(true, new List<SRClient>(_clients.Values)));
                }
                catch (Exception ex)
                {
                    Logger.Info(ex, "Exception Publishing Client Update After Disconnect");
                }
            }
            else
            {
                Logger.Info("Removed Disconnected Unknown Client");
            }

        }



        public void HandleMessage(SRSClientSession state, NetworkMessage message)
        {
            try
            {
                Logger.Debug($"Received:  Msg - {message.MsgType} from {state.SRSGuid}");

                if (!HandleConnectedClient(state, message))
                {
                    Logger.Info($"Invalid Client - disconnecting {state.SRSGuid}");
                }

                switch (message.MsgType)
                {
                    case NetworkMessage.MessageType.PING:
                        // Do nothing for now
                        break;
                    case NetworkMessage.MessageType.UPDATE:
                        HandleClientMetaDataUpdate(state, message, true);
                        break;
                    case NetworkMessage.MessageType.RADIO_UPDATE:
                        bool showTuned = _serverSettings.GetGeneralSetting(ServerSettingsKeys.SHOW_TUNED_COUNT)
                            .BoolValue;
                        HandleClientMetaDataUpdate(state, message, !showTuned);
                        HandleClientRadioUpdate(state, message, showTuned);
                        break;
                    case NetworkMessage.MessageType.SYNC:
                        HandleRadioClientsSync(state, message);
                        break;
                    case NetworkMessage.MessageType.SERVER_SETTINGS:
                        HandleServerSettingsMessage();
                        break;
                    case NetworkMessage.MessageType.EXTERNAL_AWACS_MODE_PASSWORD:
                        break;
                    case NetworkMessage.MessageType.EXTERNAL_AWACS_MODE_DISCONNECT:
                        break;
                    default:
                        Logger.Warn("Recevied unknown message type");
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Exception Handling Message " + ex.Message);
            }
        }

        private bool HandleConnectedClient(SRSClientSession state, NetworkMessage message)
        {
            var srClient = message.Client;
            if (!_clients.ContainsKey(srClient.ClientGuid))
            {
                var clientIp = (IPEndPoint)state.Socket.RemoteEndPoint;
                if (message.Version == null)
                {
                    Logger.Warn("Disconnecting Unversioned Client -  " + clientIp.Address + " " +
                                clientIp.Port);
                    state.Disconnect();
                    return false;
                }

                var clientVersion = Version.Parse(message.Version);
                var protocolVersion = Version.Parse(UpdaterChecker.MINIMUM_PROTOCOL_VERSION);

                if (clientVersion < protocolVersion)
                {
                    Logger.Warn(
                        $"Disconnecting Unsupported  Client Version - Version {clientVersion} IP {clientIp.Address} Port {clientIp.Port}");
                    HandleVersionMismatch(state);

                    //close socket after
                    state.Disconnect();

                    return false;
                }

                srClient.ClientSession = state;

                //add to proper list
                _clients[srClient.ClientGuid] = srClient;

                state.SRSGuid = srClient.ClientGuid;
                

                _eventAggregator.PublishOnUIThread(new ServerStateMessage(true,
                    new List<SRClient>(_clients.Values)));
            }

            return true;
        }

        private void HandleServerSettingsMessage()
        {
            //send server settings
            var replyMessage = new NetworkMessage
            {
                MsgType = NetworkMessage.MessageType.SERVER_SETTINGS,
                ServerSettings = _serverSettings.ToDictionary()
            };

            Multicast(replyMessage.Encode());

        }

        private void HandleVersionMismatch(SRSClientSession session)
        {
            //send server settings
            var replyMessage = new NetworkMessage
            {
                MsgType = NetworkMessage.MessageType.VERSION_MISMATCH,
            };
            session.Send(replyMessage.Encode());
        }

        private void HandleClientMetaDataUpdate(SRSClientSession session, NetworkMessage message, bool send)
        {
            if (_clients.ContainsKey(message.Client.ClientGuid))
            {
                var client = _clients[message.Client.ClientGuid];

                if (client != null)
                {
                    bool redrawClientAdminList = client.Name != message.Client.Name || client.Coalition != message.Client.Coalition;

                    //copy the data we need
                    client.LastUpdate = DateTime.Now.Ticks;
                    client.Name = message.Client.Name;
                    client.Coalition = message.Client.Coalition;
                    client.LatLngPosition = message.Client.LatLngPosition;
                    client.Seat = message.Client.Seat;

                    //send update to everyone
                    //Remove Client Radio Info
                    var replyMessage = new NetworkMessage
                    {
                        MsgType = NetworkMessage.MessageType.UPDATE,
                        Client = new SRClient
                        {
                            ClientGuid = client.ClientGuid,
                            Coalition = client.Coalition,
                            Name = client.Name,
                            LatLngPosition = client.LatLngPosition,
                            Seat = client.Seat
                        }
                    };

                    if (send)
                        Multicast(replyMessage.Encode());

                    // Only redraw client admin UI of server if really needed
                    if (redrawClientAdminList)
                    {
                        _eventAggregator.PublishOnUIThread(new ServerStateMessage(true,
                            new List<SRClient>(_clients.Values)));
                    }
                }
            }
        }

        private void HandleClientDisconnect(SRSClientSession srsSession, SRClient client)
        {
            var message = new NetworkMessage()
            {
                Client = client,
                MsgType = NetworkMessage.MessageType.CLIENT_DISCONNECT
            };

            MulticastAllExeceptOne(message.Encode(), srsSession.Id);
            try
            {
                srsSession.Dispose();
            }
            catch (Exception) { }
          
        }

        private void HandleClientRadioUpdate(SRSClientSession session, NetworkMessage message, bool send)
        {
            if (_clients.ContainsKey(message.Client.ClientGuid))
            {
                var client = _clients[message.Client.ClientGuid];

                if (client != null)
                {
                    //shouldnt be the case but just incase...
                    if (message.Client.RadioInfo == null)
                    {
                        message.Client.RadioInfo = new PlayerRadioInfo();
                    }
                    //update to local ticks
                    message.Client.RadioInfo.LastUpdate = DateTime.Now.Ticks;

                    var changed = false;

                    if (client.RadioInfo == null)
                    {
                        client.RadioInfo = message.Client.RadioInfo;
                        changed = true;
                    }
                    else
                    {
                        changed = !client.RadioInfo.Equals(message.Client.RadioInfo);
                    }


                    if (!changed)
                    {
                        changed = client.Name != message.Client.Name || client.Coalition != message.Client.Coalition
                                                                     || !message.Client.LatLngPosition.Equals(client.LatLngPosition)
                                                                     || message.Client.Seat != client.Seat;
                    }

                    client.LastUpdate = DateTime.Now.Ticks;
                    client.Name = message.Client.Name;
                    client.Coalition = message.Client.Coalition;
                    client.Seat = message.Client.Seat;
                    client.RadioInfo = message.Client.RadioInfo;
                    client.LatLngPosition = message.Client.LatLngPosition;
                    client.Seat = message.Client.Seat;

                    TimeSpan lastSent = new TimeSpan(DateTime.Now.Ticks - client.LastRadioUpdateSent);

                    //send update to everyone
                    //Remove Client Radio Info
                    if (send)
                    {
                        NetworkMessage replyMessage;
                        if ((changed || lastSent.TotalSeconds > 180))
                        {
                            client.LastRadioUpdateSent = DateTime.Now.Ticks;
                            replyMessage = new NetworkMessage
                            {
                                MsgType = NetworkMessage.MessageType.RADIO_UPDATE,
                                Client = new SRClient
                                {
                                    ClientGuid = client.ClientGuid,
                                    Coalition = client.Coalition,
                                    Name = client.Name,
                                    LatLngPosition = client.LatLngPosition,
                                    RadioInfo = client.RadioInfo, //send radio info
                                    Seat = client.Seat
                                }
                            };
                            Multicast(replyMessage.Encode());
                        }
                    }
                }
            }
        }

        private void HandleRadioClientsSync(SRSClientSession session, NetworkMessage message)
        {
            //store new client
            var replyMessage = new NetworkMessage
            {
                MsgType = NetworkMessage.MessageType.SYNC,
                Clients = new List<SRClient>(_clients.Values),
                ServerSettings = _serverSettings.ToDictionary(),
                Version = UpdaterChecker.VERSION
            };

            session.Send(replyMessage.Encode());

            //send update to everyone
            //Remove Client Radio Info
            var update = new NetworkMessage
            {
                MsgType = NetworkMessage.MessageType.UPDATE,
                Client = new SRClient
                {
                    ClientGuid = message.Client.ClientGuid,
                    RadioInfo = message.Client.RadioInfo,
                    Name = message.Client.Name,
                    Coalition = message.Client.Coalition,
                    Seat = message.Client.Seat,
                    LatLngPosition = message.Client.LatLngPosition
                }
            };

            Multicast(update.Encode());
        }

     


        public void RequestStop()
        {
          

            try
            {
                DisconnectAll();
                Stop();
                _clients.Clear();
            }
            catch (Exception ex)
            {
            }
        }
    }
}