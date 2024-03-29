﻿using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.UI;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Esri.ArcGISRuntime.UI.Controls;
using static DisplayAMap.MainWindow;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using System.Diagnostics;
using System.Collections.Concurrent;


namespace DisplayAMap
{
    internal class MapViewModel : INotifyPropertyChanged
    {
        private Timer _trainInfoTimer;
        private Timer _trainTimetableTimer;
        internal static FeatureLayer? _tracks;
        internal static FeatureLayer? _trains;
        public DataHandler _data;
        public LayerHandler _layer;
        public ClickHandler _click;
        public QueryParameters _query;
        static int i = 0;

        public MapViewModel()
        {
            // Set up static class instances for our supporting classes.
            _data = new DataHandler();
            _layer = new LayerHandler();
            _click = new ClickHandler();
            _query = new QueryParameters() { WhereClause = "1=1" };

            //Start the main process, using information from the NS API
            SetupMap(NSAPICalls.GetTrainData());
        }

        private async void SetupMap(string trainInfo)
        {
            // Set basemap
            _map = new Map(BasemapStyle.ArcGISTopographic);

            // Check if GDB exists. If GDB doesn't exist: Create GDB and train feature table. If GDB exists: Delete train feature table (we have fresh data) 
            await _layer.CreateOrPurgeGeodatabase();

            // Process the fresh data from the API into a layer
            _trains = await _data.ProcessTrainInfo(trainInfo, null, null);

            // Check if the track feature layer exists within the GDB. If doesn't exist: Call the NS API and create it. If it does exist: Load it.
            _tracks = await _layer.CreateOrFetchTracks();

            // Add both layers to our Map
            _map.OperationalLayers.Add(_tracks);
            _map.OperationalLayers.Add(_trains);

            // Set a (protected) copy of the MapView from the Main.
            _mainMapView = CopyMainMapView.Copy;

            // Set the initial viewpoint to focus on the Netherlands
            Envelope netherlandsExtent = new Envelope(3.314971, 50.803721, 7.092536, 53.510403, SpatialReferences.Wgs84);
            Viewpoint initialViewpoint = new Viewpoint(netherlandsExtent);
            _mainMapView.SetViewpoint(initialViewpoint);

            // We need the MapView to assign it a click event that fires every time the map is clicked.
            _mainMapView.GeoViewTapped += (sender, e) => _click.MyFeatureLayer_GeoViewTapped(sender, e, MainMapView, _query);

            // Everything is prepared, time to kick off the main repeating function responsible for making the trains move
            TrainInfoRepeater();
            TimetableInfoRepeater();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private Map? _map;
        public Map? Map
        {
            get { return _map; }
            set
            {
                _map = value;
                OnPropertyChanged();
            }
        }

        private GraphicsOverlayCollection? _graphicsOverlays;
        public GraphicsOverlayCollection? GraphicsOverlays
        {
            get { return _graphicsOverlays; }
            set
            {
                _graphicsOverlays = value;
                OnPropertyChanged();
            }
        }

        private MapView _mainMapView;
        public MapView MainMapView
        {
            get { return _mainMapView; }
            set
            {
                _mainMapView = value;
                // Perform any additional setup or binding logic if needed
            }
        }

        private readonly SemaphoreSlim _trainInfoSemaphore = new SemaphoreSlim(1, 1);

        private readonly SemaphoreSlim _trainTimeTableSemaphore = new SemaphoreSlim(1, 1);
        private void TrainInfoRepeater()
        {
            _trainInfoTimer = new Timer(
                async state =>
                {
                    await _trainInfoSemaphore.WaitAsync();
                    try
                    {
                        await _data.KeepUpdatingTrainPosition(state, _trains, _trains.FeatureTable.QueryFeaturesAsync(_query).Result, _tracks.FeatureTable.QueryFeaturesAsync(_query).Result, MainMapView);
                    }
                    finally
                    {
                        _trainInfoSemaphore.Release();
                    }
                },
                null,
                0,
                10000
            );
        }

        private async void TimetableInfoRepeater()
        {
            await _trainTimeTableSemaphore.WaitAsync();
            try
            {
                // Divide trainFeatures into smaller batches
                var batches = Partitioner.Create(_trains.FeatureTable.QueryFeaturesAsync(_query).Result).GetPartitions(_trains.FeatureTable.QueryFeaturesAsync(_query).Result.Count());
                var tasks = new List<Task>();

                // Process each batch concurrently
                foreach (var batch in batches)
                {
                    var task = Task.Run(async () =>
                    {
                        while (batch.MoveNext())
                        {
                            var feature = batch.Current;

                            // Introduce a delay before processing each feature
                            int delayMilliseconds = 1000; // Adjust this value as needed
                            await Task.Delay(delayMilliseconds);

                            await _data.ProcessFeature(feature, _trains);
                        }
                    });
                    tasks.Add(task);
                }
                // Wait for all tasks to complete
                await Task.WhenAll(tasks);
            }
            finally
            {
                _trainTimeTableSemaphore.Release();
            }
        }
    }
}
