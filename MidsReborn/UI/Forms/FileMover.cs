using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Mids_Reborn.UI.Forms
{
    public partial class FileMover : Form
    {
        private readonly BackgroundWorker _progressWorker = new();
        private bool _moveExceptionRequested;
        private int _copiedCount;
        private readonly string _sourceDirectory;
        private readonly string _destinationDirectory;
        private readonly List<Item> _items = new();

        private struct Item
        {
            public string Source;
            public string Directory;
            public string Destination;

            public Item(string source, string directory, string destination)
            {
                Source = source;
                Directory = directory;
                Destination = destination;
            }
        }

        public FileMover(string source, string destination)
        {
            _sourceDirectory = source;
            _destinationDirectory = destination;
            Load += OnLoad;
            InitializeComponent();

        }

        private void OnLoad(object? sender, EventArgs e)
        {
            CenterToParent();
            sourceLabel.Text = _sourceDirectory;
            destLabel.Text = _destinationDirectory;
            ExecuteDiscovery();
            ctlProgressBar1.Maximum = _items.Count;
            ctlProgressBar1.Value = 0;
            ctlProgressBar1.Step = 1;
            _progressWorker.WorkerReportsProgress = true;
            _progressWorker.DoWork += ProgressWorker_DoWork;
            _progressWorker.ProgressChanged += ProgressWorker_ProgressChanged;
            _progressWorker.RunWorkerCompleted += ProgressWorker_RunWorkerCompleted;
            Start();
        }

        private void ExecuteDiscovery()
        {
            var files = Directory.GetFiles(_sourceDirectory, "*.*", SearchOption.AllDirectories).ToList();
            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                if (!fileInfo.Exists)
                {
                    continue;
                }

                // Keep the full path relative to the source root so nested folders
                // (and identically named folders under different parents) are preserved.
                var relativePath = Path.GetRelativePath(_sourceDirectory, fileInfo.FullName);
                var relativeDir = Path.GetDirectoryName(relativePath) ?? string.Empty;
                _items.Add(new Item(fileInfo.FullName, relativeDir, Path.Combine(_destinationDirectory, relativePath)));
            }
        }

        private bool DestinationHasStructure => _items.Any(x => !string.IsNullOrWhiteSpace(x.Directory));

        private void Start()
        {
            _progressWorker.RunWorkerAsync();
        }

        private void ProgressWorker_DoWork(object? sender, DoWorkEventArgs e)
        {
            if (DestinationHasStructure)
            {
                ctlProgressBar1.StatusText = @"Recreating Directory Structure...";
                foreach (var path in from item in _items where !string.IsNullOrWhiteSpace(item.Directory) select Path.Combine(_destinationDirectory, item.Directory) into path where !Directory.Exists(path) select path)
                {
                    Directory.CreateDirectory(path);
                }
            }
            ctlProgressBar1.StatusText = @"Copying Builds...";
            ctlProgressBar1.ItemCount = _items.Count;
            for (var itemIndex = 0; itemIndex < _items.Count; itemIndex++)
            {
                var item = _items[itemIndex];
                try
                {
                    File.Copy(item.Source, item.Destination); // File.Move()
                    if (File.Exists(item.Destination))
                    {
                        _copiedCount++;
                    }
                    else
                    {
                        _moveExceptionRequested = true;
                    }
                }
                catch (Exception)
                {
                    _moveExceptionRequested = true;
                }

                var percentage = (itemIndex * ctlProgressBar1.Maximum) / _items.Count;
                _progressWorker.ReportProgress(percentage);
                Thread.Sleep(50);
            }
        }

        private void ProgressWorker_ProgressChanged(object? sender, ProgressChangedEventArgs e)
        {
            ctlProgressBar1.Value = e.ProgressPercentage;
        }

        private void ProgressWorker_RunWorkerCompleted(object? sender, RunWorkerCompletedEventArgs e)
        {
            ctlProgressBar1.StatusText = string.Empty;
            // Only report success when the worker finished cleanly and every file was copied;
            // the caller offers to delete the originals on Yes.
            var succeeded = e.Error == null && !e.Cancelled && !_moveExceptionRequested && _copiedCount == _items.Count;
            DialogResult = succeeded
                ? DialogResult.Yes
                : DialogResult.No;
        }
    }
}
