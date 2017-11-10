﻿// Copyright 2017 Esri.
//
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// You may obtain a copy of the License at: http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an
// "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific
// language governing permissions and limitations under the License.

using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Tasks;
using Esri.ArcGISRuntime.Tasks.Offline;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace ArcGISRuntime.UWP.Samples.GeodatabaseTransactions
{
    public partial class GeodatabaseTransactions
    {
        // url for the editable feature service
        private const string SyncServiceUrl = "https://sampleserver6.arcgisonline.com/arcgis/rest/services/Sync/SaveTheBaySync/FeatureServer/";

        // work in a small extent south of Galveston, TX
        private Envelope _extent = new Envelope(-95.3035, 29.0100, -95.1053, 29.1298, SpatialReferences.Wgs84);

        // store the local geodatabase to edit
        private Geodatabase _localGeodatabase;

        // store references to two tables to edit: Birds and Marine points
        private GeodatabaseFeatureTable _birdTable;
        private GeodatabaseFeatureTable _marineTable;

        public GeodatabaseTransactions()
        {
            InitializeComponent();

            // when the map view loads, add a new map
            MyMapView.Loaded += (s, e) =>
            {
                // create a new map with the oceans basemap and add it to the map view
                var map = new Map(Basemap.CreateOceans());
                MyMapView.Map = map;
            };

            // when the spatial reference changes (the map loads) add the local geodatabase tables as feature layers
            MyMapView.SpatialReferenceChanged += async (s, e) =>
            {
                // call a function (and await it) to get the local geodatabase (or generate it from the feature service)
                await GetLocalGeodatabase();

                // once the local geodatabase is available, load the tables as layers to the map
                LoadLocalGeodatabaseTables();
            };
        }

        private async Task GetLocalGeodatabase()
        {
            // get the path to the local geodatabase for this platform (temp directory, for example)
            var localGeodatabasePath = GetGdbPath();

            try
            {
                // see if the geodatabase file is already present
                if (System.IO.File.Exists(localGeodatabasePath))
                {
                    // if the geodatabase is already available, open it, hide the progress control, and update the message
                    _localGeodatabase = await Geodatabase.OpenAsync(localGeodatabasePath);
                    LoadingProgressBar.Visibility = Visibility.Collapsed;
                    MessageTextBlock.Text = "Using local geodatabase from '" + _localGeodatabase.Path + "'";
                }
                else
                {
                    // create a new GeodatabaseSyncTask with the uri of the feature server to pull from
                    var uri = new Uri(SyncServiceUrl);
                    var gdbTask = await GeodatabaseSyncTask.CreateAsync(uri);

                    // create parameters for the task: layers and extent to include, out spatial reference, and sync model
                    var gdbParams = await gdbTask.CreateDefaultGenerateGeodatabaseParametersAsync(_extent);
                    gdbParams.OutSpatialReference = MyMapView.SpatialReference;
                    gdbParams.SyncModel = SyncModel.Layer;
                    gdbParams.LayerOptions.Clear();
                    gdbParams.LayerOptions.Add(new GenerateLayerOption(0));
                    gdbParams.LayerOptions.Add(new GenerateLayerOption(1));

                    // create a geodatabase job that generates the geodatabase
                    GenerateGeodatabaseJob generateGdbJob = gdbTask.GenerateGeodatabase(gdbParams, localGeodatabasePath);

                    // handle the job changed event and check the status of the job; store the geodatabase when it's ready
                    generateGdbJob.JobChanged += (s, e) =>
                    {
                        // see if the job succeeded
                        if (generateGdbJob.Status == JobStatus.Succeeded)
                        {
                            this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                            {
                                // hide the progress control and update the message
                                LoadingProgressBar.Visibility = Visibility.Collapsed;
                                MessageTextBlock.Text = "Created local geodatabase";
                            });
                        }
                        else if (generateGdbJob.Status == JobStatus.Failed)
                        {
                            this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                            {
                                // hide the progress control and report the exception
                                LoadingProgressBar.Visibility = Visibility.Collapsed;
                                MessageTextBlock.Text = "Unable to create local geodatabase: " + generateGdbJob.Error.Message;
                            });
                        }
                    };

                    // start the generate geodatabase job
                    _localGeodatabase = await generateGdbJob.GetResultAsync();
                }
            }
            catch (Exception ex)
            {
                // show a message for the exception encountered
                this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => 
                {
                    MessageDialog dialog = new MessageDialog("Unable to create offline database: " + ex.Message);
                    dialog.ShowAsync();
                });
            }
        }

        // function that loads the two point tables from the local geodatabase and displays them as feature layers
        private async void LoadLocalGeodatabaseTables()
        {
            if(_localGeodatabase == null) { return; }

            // read the geodatabase tables and add them as layers
            foreach (GeodatabaseFeatureTable table in _localGeodatabase.GeodatabaseFeatureTables)
            {
                // load the table so the TableName can be read
                await table.LoadAsync();

                // store a reference to the Birds table
                if (table.TableName.ToLower().Contains("birds"))
                {
                    _birdTable = table;
                }

                // store a reference to the Marine table
                if (table.TableName.ToLower().Contains("marine"))
                {
                    _marineTable = table;
                }

                // create a new feature layer to show the table in the map
                var layer = new FeatureLayer(table);
                this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => MyMapView.Map.OperationalLayers.Add(layer));
            }

            // handle the transaction status changed event
            _localGeodatabase.TransactionStatusChanged += GdbTransactionStatusChanged;

            // zoom the map view to the extent of the generated local datasets
            this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                MyMapView.SetViewpointGeometryAsync(_marineTable.Extent);
                StartEditingButton.IsEnabled = true;
            });
        }

        private void GdbTransactionStatusChanged(object sender, TransactionStatusChangedEventArgs e)
        {
            // update UI controls based on whether the geodatabase has a current transaction
            this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                // these buttons should be enabled when there IS a transaction
                AddBirdButton.IsEnabled = e.IsInTransaction;
                AddMarineButton.IsEnabled = e.IsInTransaction;
                StopEditingButton.IsEnabled = e.IsInTransaction;

                // these buttons should be enabled when there is NOT a transaction
                StartEditingButton.IsEnabled = !e.IsInTransaction;
                SyncEditsButton.IsEnabled = !e.IsInTransaction;
            });
        }

        private string GetGdbPath()
        {
            // Get the UWP-specific path for storing the geodatabase
            string folder = Windows.Storage.ApplicationData.Current.LocalFolder.Path.ToString();
            return Path.Combine(folder, "savethebay.geodatabase");
        }

        private void BeginTransaction(object sender, RoutedEventArgs e)
        {
            // see if there is a transaction active for the geodatabase
            if (!_localGeodatabase.IsInTransaction)
            {
                // if not, begin a transaction
                _localGeodatabase.BeginTransaction();
                MessageTextBlock.Text = "Transaction started";
            }
        }

        private async void AddNewFeature(object sender, RoutedEventArgs args)
        {
            // See if it was the "Birds" or "Marine" button that was clicked
            Button addFeatureButton = sender as Button;

            try
            {
                // cancel execution of the sketch task if it is already active
                if (MyMapView.SketchEditor.CancelCommand.CanExecute(null))
                {
                    MyMapView.SketchEditor.CancelCommand.Execute(null);
                }

                // store the correct table to edit (for the button clicked)
                GeodatabaseFeatureTable editTable = null;
                if (addFeatureButton == AddBirdButton)
                {
                    editTable = _birdTable;
                }
                else if (addFeatureButton == AddMarineButton)
                {
                    editTable = _marineTable;
                }

                // inform the user which table is being edited
                MessageTextBlock.Text = "Click the map to add a new feature to the geodatabase table '" + editTable.TableName + "'";

                // create a random value for the 'type' attribute (integer between 1 and 7)
                Random random = new Random(DateTime.Now.Millisecond);
                int featureType = random.Next(1, 7);

                // use the sketch editor to allow the user to draw a point on the map
                MapPoint clickPoint = await MyMapView.SketchEditor.StartAsync(Esri.ArcGISRuntime.UI.SketchCreationMode.Point, false) as MapPoint;

                // create a new feature (row) in the selected table
                Feature newFeature = editTable.CreateFeature();

                // set the geometry with the point the user clicked and the 'type' with the random integer
                newFeature.Geometry = clickPoint;
                newFeature.SetAttributeValue("type", featureType);

                // add the new feature to the table
                await editTable.AddFeatureAsync(newFeature);

                // clear the message
                MessageTextBlock.Text = "New feature added to the '" + editTable.TableName + "' table";
            }
            catch (TaskCanceledException)
            {
                // ignore if the edit was canceled
            }
            catch (Exception ex)
            {
                // report other exception messages
                MessageTextBlock.Text = ex.Message;
            }
        }

        private async void StopEditTransaction(object sender, RoutedEventArgs e)
        {
            // create a new dialog that prompts for commit, rollback, or cancel
            MessageDialog promptDialog = new MessageDialog("Commit your edits to the local geodatabase or rollback to discard them.", "Stop Editing");
            UICommand commitCommand = new UICommand("Commit");
            UICommand rollbackCommand = new UICommand("Rollback");
            UICommand cancelCommand = new UICommand("Cancel");
            promptDialog.Options = MessageDialogOptions.None;
            promptDialog.Commands.Add(commitCommand);
            promptDialog.Commands.Add(rollbackCommand);
            promptDialog.Commands.Add(cancelCommand);

            // ask the user if they want to commit or rollback the transaction (or cancel to keep working in the transaction)
            IUICommand cmd = await promptDialog.ShowAsync();

            if (cmd == commitCommand)
            {
                // see if there is a transaction active for the geodatabase
                if (_localGeodatabase.IsInTransaction)
                {
                    // if there is, commit the transaction to store the edits (this will also end the transaction)
                    _localGeodatabase.CommitTransaction();
                    MessageTextBlock.Text = "Edits were committed to the local geodatabase.";
                }
            }
            else if (cmd == rollbackCommand)
            {
                // see if there is a transaction active for the geodatabase
                if (_localGeodatabase.IsInTransaction)
                {
                    // if there is, rollback the transaction to discard the edits (this will also end the transaction)
                    _localGeodatabase.RollbackTransaction();
                    MessageTextBlock.Text = "Edits were rolled back and not stored to the local geodatabase.";
                }
            }
            else
            {
                // for 'cancel' don't end the transaction with a commit or rollback
            }
        }

        // change which controls are enabled if the user chooses to require/not require transactions for edits
        private void RequireTransactionChanged(object sender, RoutedEventArgs e)
        {
            // if the local geodatabase isn't created yet, return
            if (_localGeodatabase == null) { return; }

            // get the value of the "require transactions" checkbox
            bool mustHaveTransaction = RequireTransactionCheckBox.IsChecked == true;

            // warn the user if disabling transactions while a transaction is active
            if (!mustHaveTransaction && _localGeodatabase.IsInTransaction)
            {
                MessageDialog dialog = new MessageDialog("Stop editing to end the current transaction.", "Current Transaction");
                dialog.ShowAsync();
                RequireTransactionCheckBox.IsChecked = true;
                return;
            }

            // enable or disable controls according to the checkbox value
            StartEditingButton.IsEnabled = mustHaveTransaction;
            StopEditingButton.IsEnabled = mustHaveTransaction && _localGeodatabase.IsInTransaction;
            AddBirdButton.IsEnabled = !mustHaveTransaction;
            AddMarineButton.IsEnabled = !mustHaveTransaction;
        }

        // synchronize edits in the local geodatabase with the service
        public async void SynchronizeEdits(object sender, RoutedEventArgs e)
        {
            // show the progress bar while the sync is working
            LoadingProgressBar.Visibility = Visibility.Visible;

            try
            {
                // create a sync task with the URL of the feature service to sync
                var syncTask = await GeodatabaseSyncTask.CreateAsync(new Uri(SyncServiceUrl));

                // create sync parameters
                var taskParameters = await syncTask.CreateDefaultSyncGeodatabaseParametersAsync(_localGeodatabase);

                // create a synchronize geodatabase job, pass in the parameters and the geodatabase
                SyncGeodatabaseJob job = syncTask.SyncGeodatabase(taskParameters, _localGeodatabase);

                // handle the JobChanged event for the job
                job.JobChanged += (s, arg) =>
                {
                    // report changes in the job status
                    if (job.Status == JobStatus.Succeeded)
                    {
                        // report success ...
                        Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => MessageTextBlock.Text = "Synchronization is complete!");
                    }
                    else if (job.Status == JobStatus.Failed)
                    {
                        // report failure ...
                        Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => MessageTextBlock.Text = job.Error.Message);
                    }
                    else
                    {
                        // report that the job is in progress ...
                        Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => MessageTextBlock.Text = "Sync in progress ...");
                    }
                };

                // await the completion of the job
                var result = await job.GetResultAsync();
            }
            catch (Exception ex)
            {
                // show the message if an exception occurred
                MessageTextBlock.Text = "Error when synchronizing: " + ex.Message;
            }
            finally
            {
                // hide the progress bar when the sync job is complete
                LoadingProgressBar.Visibility = Visibility.Collapsed;
            }
        }
    }
}