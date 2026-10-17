using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Mids_Reborn.Core.Base.Master_Classes;
using Mids_Reborn.Core.Utils;
using Mids_Reborn.UI.Forms;
using Newtonsoft.Json;

namespace Mids_Reborn.Core.BuildFile
{
    public class BuildManager
    {
        public struct LoadFileResult
        {
            public CharacterBuildData? BuildData;
            public string? ErrorMsg;
        }

        private static readonly Lazy<BuildManager> LazyInstance = new(() => new BuildManager());
        public static BuildManager Instance => LazyInstance.Value;

        internal CharacterBuildData? BuildData;
        private readonly IBuildNotifier _notifier;
        private readonly BuildPreferences _preferences;

        private BuildManager()
        {
            BuildData = CharacterBuildData.Instance;
            _notifier = new BuildNotifier();
            _preferences = BuildPreferences.Load();
        }

        public LoadFileResult LoadFromFileLight(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return new LoadFileResult { BuildData = null, ErrorMsg = "No file provided." };
            }

            if (!File.Exists(fileName))
            {
                return new LoadFileResult { BuildData = null, ErrorMsg = "Specified file doesn't exist." };
            }

            // Previews only; never replace the active build data here
            CharacterBuildData buildData;
            try
            {
                buildData = Deserialize(File.ReadAllText(fileName));
            }
            catch (Exception)
            {
                return new LoadFileResult { BuildData = null, ErrorMsg = "Unable to load build from file." };
            }

            var metaData = buildData.BuiltWith;
            if (metaData == null)
            {
                return new LoadFileResult { BuildData = null, ErrorMsg = "Unable to read metadata." };
            }

            if (DatabaseAPI.DatabaseName != metaData.Database)
            {
                return new LoadFileResult { BuildData = null, ErrorMsg = $"This build has been made with {metaData.Database} and doesn't match current database ({DatabaseAPI.DatabaseName})." };
            }

            return new LoadFileResult { BuildData = buildData, ErrorMsg = null };
        }

        private static CharacterBuildData Deserialize(string data)
        {
            var settings = new JsonSerializerSettings
            {
                Converters = new List<JsonConverter> { new EnhancementDataConverter() }
            };

            return JsonConvert.DeserializeObject<CharacterBuildData>(data, settings) ?? throw new InvalidOperationException("No build data found.");
        }

        /// <summary>
        /// Validates the candidate build, lets the caller confirm and clear the active build (beforeLoad), then loads it.
        /// Nothing is changed when validation fails or beforeLoad returns false.
        /// </summary>
        private bool CommitBuild(CharacterBuildData buildData, string? id, Func<bool>? beforeLoad)
        {
            if (!buildData.ValidateForLoad(out var error))
            {
                _notifier.ShowError($"Cannot load build - {error}");
                return false;
            }

            if (beforeLoad != null && !beforeLoad())
            {
                return false;
            }

            BuildData = buildData;
            BuildData.Id = id;
            return BuildData.LoadBuild();
        }

        /// <summary>
        /// Forget the remote share id, for when the active build is replaced by one that wasn't fetched from the share service.
        /// </summary>
        public void ClearShareId()
        {
            if (BuildData != null) BuildData.Id = null;
        }

        public bool LoadFromFile(string? fileName, Func<bool>? beforeLoad = null)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            var returnedVal = false;
            CharacterBuildData buildData;
            try
            {
                buildData = Deserialize(File.ReadAllText(fileName));
            }
            catch (Exception ex)
            {
                _notifier.ShowError($"Cannot load {fileName} - error reading build data.\r\n\r\n{ex.Message}");
                return false;
            }

            var metaData = buildData.BuiltWith;
            if (metaData == null)
            {
                _notifier.ShowError($"Cannot load {fileName} - error reading build metadata.\r\n\r\n{nameof(metaData)}");
                return false;
            }

            var fileInfo = new FileInfo(fileName);
            if (DatabaseAPI.DatabaseName != metaData.Database)
            {
                var databases = Directory.EnumerateDirectories(Path.Combine(AppContext.BaseDirectory, AppDataPaths.RoamingFolder)).ToList();
                var selected = databases.FirstOrDefault(d => d.Contains(metaData.Database));
                if (selected is null)
                {
                    _notifier.ShowError($"This build requires the {metaData.Database} be installed prior to loading it.\r\nPlease install the database and try again.");
                    return false;
                }
                var result = _notifier.ShowQuestionDialog($"This build was created using the {metaData.Database} database.\r\nDo you want to reload and switch to this database, then attempt to load the build?", fileInfo.Name, MessageBoxEx.MessageBoxExIcon.Warning);
                if (result != DialogResult.Yes)
                {
                    return returnedVal;
                }

                MidsContext.Config.LastFileName = fileName;
                MidsContext.Config.DataPath = selected;
                MidsContext.Config.SavePath = selected;
                MidsContext.Config.SaveConfig();
                Application.Restart();
            }
            else
            {
                // Compare Database Version if Enabled
                if (MidsContext.Config.WarnOnOldDbMbd)
                {
                    var outDatedDb = Helpers.IsVersionNewer(DatabaseAPI.Database.Version, metaData.DatabaseVersion);
                    var newerDb = Helpers.IsVersionNewer(metaData.DatabaseVersion, DatabaseAPI.Database.Version);
                    var continueLoad = false;

                    if (outDatedDb)
                    {
                        if (_preferences.ShouldSkipWarning(fileName))
                        {
                            continueLoad = true;
                        }
                        else
                        {
                            var result = _notifier.ShowWarningDialog($"This build was created in an older version of the {metaData.Database} database.\r\nSome powers and/or enhancements may have changed, you may need to rebuild some of it.", @"Warning", true);
                            switch (result)
                            {
                                case DialogResult.Ignore:
                                    _preferences.AddIgnoredBuild(fileName);
                                    continueLoad = true;
                                    break;
                                case DialogResult.OK:
                                    continueLoad = true;
                                    break;
                            }
                        }
                    }

                    if (newerDb)
                    {
                        if (_preferences.ShouldSkipWarning(fileName))
                        {
                            continueLoad = true;
                        }
                        else
                        {
                            var result = _notifier.ShowWarningDialog($"This build was created in an newer version of the {metaData.Database} database.\r\nIt is recommended that you update the database.", @"Warning", true);
                            switch (result)
                            {
                                case DialogResult.Ignore:
                                    _preferences.AddIgnoredBuild(fileName);
                                    continueLoad = true;
                                    break;
                                case DialogResult.OK:
                                    continueLoad = true;
                                    break;
                            }
                        }
                    }

                    if (!outDatedDb && !newerDb) continueLoad = true;

                    if (continueLoad)
                    {
                        returnedVal = CommitBuild(buildData, null, beforeLoad);
                    }
                }
                else
                {
                    returnedVal = CommitBuild(buildData, null, beforeLoad);
                }
            }

            return returnedVal;
        }

        public bool SaveToFile(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            if (MidsContext.Character?.CurrentBuild == null) return false;
            var powerEntries = MidsContext.Character.CurrentBuild.Powers.GetRange(0, 24);
            if (powerEntries.All(x => x?.Power == null))
            {
                _notifier.ShowError(@"Unable to save build, you haven't selected any powers.");
                return false;
            }

            try
            {
                BuildData?.Update(MidsContext.Character);
                FileIO.WriteAllTextAtomic(fileName, JsonConvert.SerializeObject(BuildData, Formatting.Indented));
            }
            catch (Exception ex)
            {
                _notifier.ShowError($"Unable to save build to {fileName}.\r\n\r\n{ex.Message}");
                return false;
            }

            return true;
        }

        public string GetShareData()
        {
            return BuildData != null ? BuildData.GenerateShareData() : string.Empty;
        }

        public string GetDataChunk()
        {
            return BuildData != null ? BuildData.GenerateChunkData() : string.Empty;
        }

        private bool LoadShareData(string? data, string? id = null, Func<bool>? beforeLoad = null)
        {
            if (string.IsNullOrWhiteSpace(data)) return false;
            var returnedVal = false;
            CharacterBuildData buildData;
            try
            {
                buildData = Deserialize(data);
            }
            catch (Exception ex)
            {
                _notifier.ShowError($"Cannot load share data - An error occurred during reading build data.\r\n\r\n{ex.Message}");
                return false;
            }

            var metaData = buildData.BuiltWith;
            if (metaData == null)
            {
                _notifier.ShowError($"Cannot load share data - An error occurred during reading build metadata.\r\n\r\n{nameof(metaData)}");
                return false;
            }

            if (DatabaseAPI.DatabaseName != metaData.Database)
            {
                var databases = Directory.EnumerateDirectories(Path.Combine(AppContext.BaseDirectory, AppDataPaths.RoamingFolder)).ToList();
                var selected = databases.FirstOrDefault(d => d.Contains(metaData.Database));
                if (selected is null)
                {
                    _notifier.ShowError($"This build requires the {metaData.Database} be installed prior to loading it.\r\nPlease install the database and try again.");
                }

                _notifier.ShowWarning($"This build requires the {metaData.Database}, however you are currently using the {DatabaseAPI.DatabaseName} database. Please load the correct database and try again.");
                return false;
            }

            // Compare Database Version if Enabled
            if (MidsContext.Config.WarnOnOldDbMbd)
            {
                var outDatedDb = Helpers.IsVersionNewer(DatabaseAPI.Database.Version, metaData.DatabaseVersion);
                var newerDb = Helpers.IsVersionNewer(metaData.DatabaseVersion, DatabaseAPI.Database.Version);
                var continueLoad = false;

                if (outDatedDb)
                {
                    var result = _notifier.ShowWarningDialog($"This build was created in an older version of the {metaData.Database} database.\r\nSome powers and/or enhancements may have changed, you may need to rebuild some of it.", @"Warning");
                    continueLoad = result == DialogResult.OK;
                }

                if (newerDb)
                {
                    var result = _notifier.ShowWarningDialog($"This build was created in an newer version of the {metaData.Database} database.\r\nIt is recommended that you update the database.", @"Warning");
                    continueLoad = result == DialogResult.OK;
                }

                if (!outDatedDb && !newerDb) continueLoad = true;

                if (continueLoad)
                {
                    returnedVal = CommitBuild(buildData, id, beforeLoad);
                }
            }
            else
            {
                returnedVal = CommitBuild(buildData, id, beforeLoad);
            }

            return returnedVal;
        }

        public bool ValidateAndLoadImportData(DataClassifier.ClassificationResult classificationResult, Func<bool>? beforeLoad = null)
        {
            if (!classificationResult.IsValid)
            {
                _notifier.ShowError("Invalid or unknown data format.");
                return false;
            }

            int uncompressedSize = -1, compressedSize = -1;
            var data = string.Empty;
            switch (classificationResult.Type)
            {
                case DataClassifier.DataType.Mbd or DataClassifier.DataType.Mxd:
                    // Split input to separate header and data
                    var lines = classificationResult.Content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length < 2) // Ensure there is at least one header line and data line
                    {
                        _notifier.ShowError("Input format is incorrect. No data found after header.");
                        return false;
                    }

                    if (lines.Length <= 0) return false;
                    // Clean the header by removing surrounding pipes and trimming whitespace
                    var header = lines[0].Trim('|').Trim();
                    var headerItems = header.Split(';', StringSplitOptions.RemoveEmptyEntries);
                    if (headerItems.Length != 5)
                    {
                        _notifier.ShowError("Header format is incorrect.");
                        return false;
                    }

                    if (!int.TryParse(headerItems[1], out uncompressedSize) || !int.TryParse(headerItems[2], out compressedSize) ||
                        uncompressedSize < 0 || compressedSize < 0)
                    {
                        _notifier.ShowError("Header contains invalid size information.");
                        return false;
                    }

                    data = string.Join("", lines.Skip(1)).Replace("|", "");
                    break;
                case DataClassifier.DataType.UnkBase64:
                    data = classificationResult.Content.Trim();
                    break;
                case DataClassifier.DataType.Unknown:
                default:
                    break;
            }

            
            // Process data based on the type determined by the classification
            switch (classificationResult.Type)
            {
                case DataClassifier.DataType.Mxd:
                    // Process as HEX
                    if (!Regex.IsMatch(data, @"\A\b[0-9A-F]+\b\Z", RegexOptions.IgnoreCase))
                    {
                        _notifier.ShowError("Data does not contain valid HEX values.");
                        return false;
                    }

                    var decodedBytes = ModernZlib.HexDecodeBytes(Encoding.ASCII.GetBytes(data));
                    if (decodedBytes.Length != compressedSize)
                    {
                        _notifier.ShowError("Compressed size mismatch.");
                        return false;
                    }

                    try
                    {
                        decodedBytes = ModernZlib.DecompressChunk(decodedBytes, uncompressedSize);
                    }
                    catch (Exception ex)
                    {
                        _notifier.ShowError($"Unable to decompress build data.\r\n\r\n{ex.Message}");
                        return false;
                    }

                    if (beforeLoad != null && !beforeLoad())
                    {
                        return false;
                    }

                    // Legacy data is read straight into the character, it has no share id
                    ClearShareId();
                    // Now use the decompressed bytes with the MxDReadSaveData method
                    var loadSuccess = MidsCharacterFileFormat.MxDReadSaveData(ref decodedBytes, false);
                    return loadSuccess;

                case DataClassifier.DataType.Mbd:
                    // Process as Base64
                    if (!Regex.IsMatch(data, @"^[a-zA-Z0-9\+/]*={0,3}$", RegexOptions.None))
                    {
                        _notifier.ShowError("Data is not valid BASE64.");
                        return false;
                    }

                    CompressionResult decoded;
                    try
                    {
                        decoded = Compression.DecompressFromBase64(data);
                    }
                    catch (Exception ex)
                    {
                        _notifier.ShowError($"Unable to decompress build data.\r\n\r\n{ex.Message}");
                        return false;
                    }

                    if (decoded.CompressedSize != compressedSize)
                    {
                        _notifier.ShowError("Compressed size mismatch.");
                        return false;
                    }

                    if (decoded.UncompressedSize != uncompressedSize)
                    {
                        _notifier.ShowError("Uncompressed size mismatch.");
                        return false;
                    }

                    return LoadShareData(decoded.OutString, null, beforeLoad);
                case DataClassifier.DataType.UnkBase64:
                    if (!Regex.IsMatch(data, @"^[a-zA-Z0-9\+/]*={0,3}$", RegexOptions.None))
                    {
                        _notifier.ShowError("Data is not valid BASE64.");
                        return false;
                    }

                    CompressionResult unkDecoded;
                    try
                    {
                        unkDecoded = Compression.DecompressFromBase64(data);
                    }
                    catch (Exception)
                    {
                        _notifier.ShowError("An unknown error occurred.");
                        return false;
                    }

                    return LoadShareData(unkDecoded.OutString, null, beforeLoad);
                case DataClassifier.DataType.Unknown:
                    break;
                default:
                    _notifier.ShowError("Unsupported data type detected.");
                    return false;
            }

            return false;
        }

        public bool ValidateAndLoadSchemaData(string data, string? id = null, Func<bool>? beforeLoad = null)
        {
            if (!Regex.IsMatch(data, @"^[a-zA-Z0-9\+/]*={0,3}$", RegexOptions.None))
            {
                _notifier.ShowError("Data is not valid BASE64.");
                return false;
            }

            CompressionResult decoded;
            try
            {
                decoded = Compression.DecompressFromBase64(data);
            }
            catch (Exception ex)
            {
                _notifier.ShowError($"Unable to decompress build data.\r\n\r\n{ex.Message}");
                return false;
            }

            return LoadShareData(decoded.OutString, id, beforeLoad);
        }
    }
}
