using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Caliburn.Micro;
using Ciribob.FS3D.SimpleRadio.Standalone.Common.Audio.Recording;
using Ciribob.FS3D.SimpleRadio.Standalone.Common.Network.Client;
using Ciribob.FS3D.SimpleRadio.Standalone.Common.Settings.Setting;
using Ciribob.FS3D.SimpleRadio.Standalone.Server.Network.Models;
using Ciribob.FS3D.SimpleRadio.Standalone.Server.Settings;
using Ciribob.SRS.Common.Network.Models;
using NLog;
using LogManager = NLog.LogManager;

namespace Ciribob.FS3D.SimpleRadio.Standalone.Server.Network;

internal class UDPVoiceRouter : IHandle<ServerFrequenciesChanged>, IHandle<ServerStateMessage>
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly List<int>
        _emptyBlockedRadios =
            new(); // Used in radio reachability check below, server does not track blocked radios, so forward all

    private readonly ConcurrentDictionary<string, SRClientBase> _clientsList;
    private readonly IEventAggregator _eventAggregator;
    private readonly List<double> _globalFrequencies = new();

    private readonly BlockingCollection<OutgoingUDPPackets>
        _outGoing = new();

    private readonly CancellationTokenSource _outgoingCancellationToken = new();

    private readonly CancellationTokenSource _pendingProcessingCancellationToken = new();

    private readonly BlockingCollection<PendingPacket> _pendingProcessingPackets =
        new();

    private readonly ServerSettingsStore _serverSettings = ServerSettingsStore.Instance;
    private readonly string _sessionId;
    private UdpClient _listener;
    private List<double> _recordingFrequencies = new();
    private AudioRecordingManager _recordingManager;

    private volatile bool _stop;

    private List<double> _testFrequencies = new();

    public UDPVoiceRouter(ConcurrentDictionary<string, SRClientBase> clientsList, IEventAggregator eventAggregator,
        string sessionId = "")
    {
        if (sessionId == "")
        {
            sessionId = $"{DateTime.Now.ToShortDateString().Replace("/","-")}-{DateTime.Now.ToShortTimeString().Replace(":","")}";
            Logger.Info("Session Blank - generating one");
        }
        _clientsList = clientsList;
        _eventAggregator = eventAggregator;
        _sessionId = sessionId;
        _eventAggregator.Subscribe(this);

        var freqString = _serverSettings.GetGeneralSetting(ServerSettingsKeys.TEST_FREQUENCIES).StringValue;
        UpdateTestFrequencies(freqString);

        var recordString = _serverSettings.GetGeneralSetting(ServerSettingsKeys.SERVER_RECORDING_FREQUENCIES)
            .StringValue;
        UpdateRecordingFrequencies(recordString);
    }

    public async Task HandleAsync(ServerFrequenciesChanged message, CancellationToken cancellationToken)
    {
        if (message.TestFrequencies != null) UpdateTestFrequencies(message.TestFrequencies);

        if (message.ServerRecordingFrequencies != null) UpdateRecordingFrequencies(message.ServerRecordingFrequencies);
    }

    public Task HandleAsync(ServerStateMessage message, CancellationToken cancellationToken)
    {
        if (message.DisconnectingClientGuid != null && message.IsRunning)
            _recordingManager?.RemoveClientBuffer(message.DisconnectingClientGuid);

        return Task.CompletedTask;
    }


    private void UpdateTestFrequencies(string freqString)
    {
        var freqStringList = freqString.Split(',');

        var newList = new List<double>();
        foreach (var freq in freqStringList)
            if (double.TryParse(freq.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var freqDouble))
            {
                freqDouble *= 1e+6; //convert to Hz from MHz
                newList.Add(freqDouble);
                Logger.Info("Adding Test Frequency: " + freqDouble);
            }

        _testFrequencies = newList;
    }

    private void UpdateRecordingFrequencies(string freqString)
    {
        var freqStringList = freqString.Split(',');

        var newList = new List<double>();
        foreach (var freq in freqStringList)
            if (double.TryParse(freq.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var freqDouble))
            {
                freqDouble *= 1e+6; //convert to Hz from MHz
                newList.Add(freqDouble);
                Logger.Info("Adding Recording Frequency: " + freqDouble);
            }

        _recordingFrequencies = newList;
    }


    public void Listen()
    {
        //start threads
        //packets that need processing
        new Thread(ProcessPackets).Start();
        //outgoing packets
        new Thread(SendPendingPackets).Start();

        var port = _serverSettings.GetServerPort();
        _listener = new UdpClient();
        try
        {
            _listener.AllowNatTraversal(true);
        }
        catch
        {
        }

        _listener.ExclusiveAddressUse = true;
        _listener.DontFragment = true;
        _listener.Client.DontFragment = true;
        _listener.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        while (!_stop)
            try
            {
                var groupEP = new IPEndPoint(IPAddress.Any, port);
                var rawBytes = _listener.Receive(ref groupEP);

                if (rawBytes?.Length == 22)
                    try
                    {
                        //lookup guid here
                        //22 bytes are guid!
                        var guid = Encoding.ASCII.GetString(
                            rawBytes, 0, 22);

                        if (_clientsList.ContainsKey(guid))
                        {
                            var client = _clientsList[guid];
                            client.VoipPort = groupEP;

                            //send back ping UDP
                            _listener.Send(rawBytes, rawBytes.Length, groupEP);
                        }
                    }
                    catch (Exception ex)
                    {
                        //dont log because it slows down thread too much...
                    }
                else if (rawBytes?.Length > 22)
                    _pendingProcessingPackets.Add(new PendingPacket
                    {
                        RawBytes = rawBytes,
                        ReceivedFrom = groupEP
                    });
            }
            catch (Exception e)
            {
                Logger.Error(e, "Error receving audio UDP for client " + e.Message);
            }

        try
        {
            _listener.Close();
        }
        catch (Exception e)
        {
        }
    }

    public void RequestStop()
    {
        _stop = true;
        try
        {
            _listener.Close();
        }
        catch (Exception e)
        {
        }

        _outgoingCancellationToken.Cancel();
        _pendingProcessingCancellationToken.Cancel();
    }

    private void ProcessPackets()
    {
        _recordingManager = new AudioRecordingManager(_sessionId);

        _recordingManager.Start(_recordingFrequencies);

        while (!_stop)
            try
            {
                PendingPacket udpPacket = null;
                _pendingProcessingPackets.TryTake(out udpPacket, 100000, _pendingProcessingCancellationToken.Token);

                if (udpPacket != null)
                {
                    //last 22 bytes are guid!
                    var guid = Encoding.ASCII.GetString(
                        udpPacket.RawBytes, udpPacket.RawBytes.Length - 22, 22);

                    if (_clientsList.ContainsKey(guid))
                    {
                        var client = _clientsList[guid];
                        client.VoipPort = udpPacket.ReceivedFrom;


                        if (client.Muted)
                        {
                            // IGNORE THE AUDIO
                        }
                        else
                        {
                            try
                            {
                                //decode
                                var udpVoicePacket = UDPVoicePacket.DecodeVoicePacket(udpPacket.RawBytes);

                                if (udpVoicePacket != null)
                                    //magical ping ignore message 4 - its an empty voip packet to intialise VoIP if
                                    //someone doesnt transmit
                                {
                                    var outgoingVoice = GenerateOutgoingPacket(udpVoicePacket, udpPacket, client);

                                    if (outgoingVoice != null)
                                        //Add to the processing queue
                                        _outGoing.Add(outgoingVoice);
                                    //add to the recording queue if its not intercom &
                                    for (var i = 0; i < udpVoicePacket.Modulations.Length; i++)
                                        if (ShouldRecord((Modulation)udpVoicePacket.Modulations[i],
                                                udpVoicePacket.Frequencies[i]))
                                        {
                                            _recordingManager.AddClientAudio(new ClientAudio
                                            {
                                                Modulation = udpVoicePacket.Modulations[i],
                                                Frequency = udpVoicePacket.Frequencies[i],
                                                ClientGuid = udpVoicePacket.Guid,
                                                UnitId = udpVoicePacket.UnitId,
                                                Volume = 1,
                                                EncodedAudio = udpVoicePacket.AudioPart1Bytes,
                                                IsSecondary = false,
                                                PacketNumber = udpVoicePacket.PacketNumber,
                                                //Handle it as either received on INTERCOM or not
                                                ReceivedRadio = 1,
                                                UnitType = client.UnitState.UnitType,
                                                OriginalClientGuid = udpVoicePacket.OriginalClientGuid,
                                                ReceiveTime = DateTime.Now.Ticks
                                            });
                                            break;
                                        }

                                    //mark as transmitting for the UI
                                    var mainFrequency = udpVoicePacket.Frequencies.FirstOrDefault();
                                    // Only trigger transmitting frequency update for "proper" packets (excluding invalid frequencies and magic ping packets with modulation 4)
                                    if (mainFrequency > 0)
                                    {
                                        var str = "";

                                        for (var i = 0; i < udpVoicePacket.Frequencies.Length; i++)
                                        {
                                            var mainModulation = (Modulation)udpVoicePacket.Modulations[i];

                                            if (mainModulation == Modulation.INTERCOM)
                                                str += " INTERCOM";
                                            else
                                                str +=
                                                    $" {(udpVoicePacket.Frequencies[i] / 1000000).ToString("0.000", CultureInfo.InvariantCulture)} {mainModulation}";
                                        }

                                        client.TransmittingFrequency = str;
                                        client.LastTransmissionReceived = DateTime.Now;
                                    }
                                }
                            }
                            catch (Exception)
                            {
                                //Hide for now, slows down loop to much....
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Info("Failed to Process UDP Packet: " + ex.Message);
            }

        _recordingManager.Stop();
        _recordingManager = null;
    }

    private bool ShouldRecord(Modulation modulation, double frequency)
    {
        if (Modulation.INTERCOM != modulation)
            foreach (var recordingFrequency in _recordingFrequencies)
                if (RadioBase.FreqCloseEnough(frequency, recordingFrequency))
                    return true;
        return false;
    }

    private
        void SendPendingPackets()
    {
        //_listener.Send(bytes, bytes.Length, ip);
        while (!_stop)
            try
            {
                OutgoingUDPPackets udpPacket = null;
                _outGoing.TryTake(out udpPacket, 100000, _pendingProcessingCancellationToken.Token);

                if (udpPacket != null)
                {
                    var bytes = udpPacket.ReceivedPacket;
                    var bytesLength = bytes.Length;
                    foreach (var outgoingEndPoint in udpPacket.OutgoingEndPoints)
                        try
                        {
                            _listener.Send(bytes, bytesLength, outgoingEndPoint);
                        }
                        catch (Exception ex)
                        {
                            //dont log, slows down too much...
                        }
                }
            }
            catch (Exception ex)
            {
                Logger.Info("Error processing Sending Queue UDP Packet: " + ex.Message);
            }
    }

    private OutgoingUDPPackets GenerateOutgoingPacket(UDPVoicePacket udpVoice, PendingPacket pendingPacket,
        SRClientBase fromClient)
    {
        var outgoingList = new HashSet<IPEndPoint>();


        var guid = fromClient.ClientGuid;

        foreach (var client in _clientsList)
            if (!client.Key.Equals(guid))
            {
                var ip = client.Value.VoipPort;
                var global = false;
                if (ip != null)
                {
                    for (var i = 0; i < udpVoice.Frequencies.Length; i++)
                        foreach (var testFrequency in _globalFrequencies)
                            if (RadioBase.FreqCloseEnough(testFrequency, udpVoice.Frequencies[i]))
                            {
                                //ignore everything as its global frequency
                                global = true;
                                break;
                            }

                    if (global)
                    {
                        outgoingList.Add(ip);
                    }
                    // check that either coalition radio security is disabled OR the coalitions match
                    else
                    {
                        var radioInfo = client.Value.UnitState;

                        if (radioInfo != null && radioInfo.Radios != null)
                            for (var i = 0; i < udpVoice.Frequencies.Length; i++)
                            {
                                RadioReceivingState radioReceivingState = null;
                                bool decryptable;
                                var receivingRadio = RadioBase.CanHearTransmission(udpVoice.Frequencies[i],
                                    (Modulation)udpVoice.Modulations[i],
                                    udpVoice.Encryptions[i],
                                    udpVoice.UnitId,
                                    _emptyBlockedRadios,
                                    radioInfo.Radios,
                                    radioInfo.UnitId,
                                    out radioReceivingState,
                                    out decryptable);

                                //only send if we can hear!
                                if (receivingRadio != null) outgoingList.Add(ip);
                            }
                    }
                }
            }
            else
            {
                var ip = client.Value.VoipPort;

                if (ip != null)
                    foreach (var frequency in udpVoice.Frequencies)
                    foreach (var testFrequency in _testFrequencies)
                        if (RadioBase.FreqCloseEnough(testFrequency, frequency))
                        {
                            //send back to sending client as its a test frequency
                            outgoingList.Add(ip);
                            break;
                        }
            }

        if (outgoingList.Count > 0)
            return new OutgoingUDPPackets
            {
                OutgoingEndPoints = outgoingList.ToList(),
                ReceivedPacket = pendingPacket.RawBytes
            };
        return null;
    }
}