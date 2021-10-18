﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Singletons;
using Ciribob.SRS.Common;
using Ciribob.SRS.Common.DCSState;
using Ciribob.SRS.Common.Network;
using Newtonsoft.Json;
using NLog;

/**
Keeps radio information in Sync Between DCS and

**/

namespace Ciribob.SRS.Client.Network.DCS
{
    public class DCSRadioSyncManager
    {
        private readonly SendRadioUpdate _clientRadioUpdate;
        private readonly ClientSideUpdate _clientSideUpdate;
        public static readonly string AWACS_RADIOS_FILE = "awacs-radios.json";
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly ClientStateSingleton _clientStateSingleton = ClientStateSingleton.Instance;
        private readonly DCSRadioSyncHandler _dcsRadioSyncHandler;

        public delegate void ClientSideUpdate();
        public delegate void SendRadioUpdate();

        private volatile bool _stopExternalAWACSMode;

        private readonly ConnectedClientsSingleton _clients = ConnectedClientsSingleton.Instance; 

        public bool IsListening { get; private set; }

        public DCSRadioSyncManager(SendRadioUpdate clientRadioUpdate, ClientSideUpdate clientSideUpdate,
           string guid, DCSRadioSyncHandler.NewAircraft _newAircraftCallback)
        {
            _clientRadioUpdate = clientRadioUpdate;
            _clientSideUpdate = clientSideUpdate;
            IsListening = false;
            _dcsRadioSyncHandler = new DCSRadioSyncHandler(clientRadioUpdate, _newAircraftCallback);

           
        }

      

        public void Start()
        {
            DcsListener();
            IsListening = true;
        }

        public void StartExternalAWACSModeLoop()
        {
            _stopExternalAWACSMode = false;

            RadioInformation[] awacsRadios;
            try
            {
                string radioJson = File.ReadAllText(AWACS_RADIOS_FILE);
                awacsRadios = JsonConvert.DeserializeObject<RadioInformation[]>(radioJson);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to load AWACS radio file");

                awacsRadios = new RadioInformation[11];
                for (int i = 0; i < 11; i++)
                {
                    awacsRadios[i] = new RadioInformation
                    {
                        freq = 1,
                        freqMin = 1,
                        freqMax = 1,
                        secFreq = 0,
                        modulation = RadioInformation.Modulation.DISABLED,
                        name = "No Radio",
                        freqMode = RadioInformation.FreqMode.COCKPIT,
                        volMode = RadioInformation.VolumeMode.COCKPIT
                    };
                }
            }

            // Force an immediate update of radio information
            _clientStateSingleton.LastSent = 0;

            Task.Factory.StartNew(() =>
            {
                Logger.Debug("Starting external AWACS mode loop");

                _clientStateSingleton.IntercomOffset = 1;
                while (!_stopExternalAWACSMode )
                {
                    var unitId = PlayerRadioInfo.UnitIdOffset + _clientStateSingleton.IntercomOffset;

                    //save
                    _dcsRadioSyncHandler.ProcessRadioInfo(new PlayerRadioInfo
                    {
                        LastUpdate = 0,
                        control = PlayerRadioInfo.RadioSwitchControls.HOTAS,
                        name = _clientStateSingleton.LastSeenName,
                        ptt = false,
                        radios = awacsRadios,
                        selected = 1,
                        latLng = new LatLngPosition(){lat =0,lng=0,alt=0},
                        simultaneousTransmission = false,
                        simultaneousTransmissionControl = PlayerRadioInfo.SimultaneousTransmissionControl.ENABLED_INTERNAL_SRS_CONTROLS,
                        unit = "External AWACS",
                        unitId = (uint)unitId,
                        inAircraft = false
                    });

                    Thread.Sleep(200);
                }

                var radio = new PlayerRadioInfo();
                radio.Reset();
                _dcsRadioSyncHandler.ProcessRadioInfo(radio);
                _clientStateSingleton.IntercomOffset = 1;

                Logger.Debug("Stopping external AWACS mode loop");
            });
        }

       

        private void DcsListener()
        {
            _dcsRadioSyncHandler.Start();
          
        }

        public void Stop()
        {
            _stopExternalAWACSMode = true;
            IsListening = false;

            _dcsRadioSyncHandler.Stop();

        }
    }
}