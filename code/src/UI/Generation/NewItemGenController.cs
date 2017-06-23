﻿// ******************************************************************
// Copyright (c) Microsoft. All rights reserved.
// This code is licensed under the MIT License (MIT).
// THE CODE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
// IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
// DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
// TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH
// THE CODE OR THE USE OR OTHER DEALINGS IN THE CODE.
// ******************************************************************

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

using Microsoft.TemplateEngine.Edge.Template;
using Microsoft.Templates.Core;
using Microsoft.Templates.Core.Diagnostics;
using Microsoft.Templates.Core.Gen;
using Microsoft.Templates.Core.PostActions;
using Microsoft.Templates.Core.PostActions.Catalog.Merge;
using Microsoft.VisualStudio.TemplateWizard;
using Newtonsoft.Json;

namespace Microsoft.Templates.UI
{
    public class NewItemGenController : GenController
    {
        private static Lazy<NewItemGenController> _instance = new Lazy<NewItemGenController>(Initialize);
        public static NewItemGenController Instance => _instance.Value;

        private static NewItemGenController Initialize()
        {
            return new NewItemGenController(new NewItemPostActionFactory());
        }

        private NewItemGenController(PostActionFactory postactionFactory)
        {
            _postactionFactory = postactionFactory;
        }

        public(string ProjectType, string Framework) ReadProjectConfiguration()
        {
            var path = Path.Combine(GenContext.Current.ProjectPath, "Package.appxmanifest");
            if (File.Exists(path))
            {
                var manifest = XElement.Load(path);

                var metadata = manifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "Metadata");
                var projectType = metadata?.Descendants().FirstOrDefault(m => m.Attribute("Name").Value == "projectType")?.Attribute("Value")?.Value;
                var framework = metadata?.Descendants().FirstOrDefault(m => m.Attribute("Name").Value == "framework")?.Attribute("Value")?.Value;

                return (projectType, framework);
            }
            return (string.Empty, string.Empty);
        }

        public UserSelection GetUserSelectionNewItem(TemplateType templateType)
        {
            var newItem = new Views.NewItem.MainView(templateType);

            try
            {
                CleanStatusBar();

                GenContext.ToolBox.Shell.ShowModal(newItem);
                if (newItem.Result != null)
                {
                    // TODO: Review when right-click-actions available to track Project or Page completed.
                    // AppHealth.Current.Telemetry.TrackWizardCompletedAsync(WizardTypeEnum.NewItem).FireAndForget();

                    return newItem.Result;
                }
                else
                {
                    // TODO: Review when right-click-actions available to track Project or Page cancelled.
                    // AppHealth.Current.Telemetry.TrackWizardCancelledAsync(WizardTypeEnum.NewItem).FireAndForget();
                }
            }
            catch (Exception ex) when (!(ex is WizardBackoutException))
            {
                newItem.SafeClose();
                ShowError(ex);
            }

            GenContext.ToolBox.Shell.CancelWizard();

            return null;
        }

        public async Task GenerateNewItemAsync(UserSelection userSelection)
        {
            try
            {
               await UnsafeGenerateNewItemAsync(userSelection);
            }
            catch (Exception ex)
            {
                ShowError(ex, userSelection);

                GenContext.ToolBox.Shell.CancelWizard(false);
            }
        }

        public async Task UnsafeGenerateNewItemAsync(UserSelection userSelection)
        {
            var genItems = GenComposer.ComposeNewItem(userSelection).ToList();
            var chrono = Stopwatch.StartNew();

            var genResults = await GenerateItemsAsync(genItems);

            chrono.Stop();

            // TODO: Review New Item telemetry
            TrackTelemery(genItems, genResults, chrono.Elapsed.TotalSeconds, userSelection.ProjectType, userSelection.Framework);
        }

        private TempGenerationResult CompareTempGenerationWithProject()
        {
            var result = new TempGenerationResult();
            var files = Directory
                .EnumerateFiles(GenContext.Current.OutputPath, "*", SearchOption.AllDirectories)
                .Where(f => !Regex.IsMatch(f, MergePostAction.PostactionRegex) && !Regex.IsMatch(f, MergePostAction.FailedPostactionRegex))
                .ToList();

            foreach (var file in files)
            {
                var destFilePath = file.Replace(GenContext.Current.OutputPath, GenContext.Current.ProjectPath);
                var fileName = file.Replace(GenContext.Current.OutputPath + Path.DirectorySeparatorChar, string.Empty);

                var projectFileName = Path.GetFullPath(Path.Combine(GenContext.Current.ProjectPath, fileName));

                if (File.Exists(projectFileName))
                {
                    if (GenContext.Current.MergeFilesFromProject.ContainsKey(fileName))
                    {
                        if (FilesAreEqual(file, destFilePath))
                        {
                            if (!GenContext.Current.GenerationWarnings.Any(g => g.FileName == fileName))
                            {
                                GenContext.Current.MergeFilesFromProject.Remove(fileName);
                                result.UnchangedFiles.Add(fileName);
                            }
                        }
                        else
                        {
                            result.ModifiedFiles.Add(fileName);
                        }
                    }
                    else
                    {
                        if (FilesAreEqual(file, destFilePath))
                        {
                            result.UnchangedFiles.Add(fileName);
                        }
                        else
                        {
                            result.ConflictingFiles.Add(fileName);
                        }
                    }
                }
                else
                {
                    result.NewFiles.Add(fileName);
                }
            }

            return result;
        }

        public NewItemGenerationResult CompareOutputAndProject()
        {
            var compareResult = CompareTempGenerationWithProject();
            var result = new NewItemGenerationResult();
            result.NewFiles.AddRange(compareResult.NewFiles.Select(n =>
                new NewItemGenerationFileInfo(
                        n,
                        Path.Combine(GenContext.Current.OutputPath, n),
                        Path.Combine(GenContext.Current.ProjectPath, n))));

            result.ConflictingFiles.AddRange(compareResult.ConflictingFiles.Select(n =>
                new NewItemGenerationFileInfo(
                        n,
                        Path.Combine(GenContext.Current.OutputPath, n),
                        Path.Combine(GenContext.Current.ProjectPath, n))));

            result.ModifiedFiles.AddRange(compareResult.ModifiedFiles.Select(n =>
                new NewItemGenerationFileInfo(
                      n,
                      Path.Combine(GenContext.Current.OutputPath, n),
                      Path.Combine(GenContext.Current.ProjectPath, n))));

            result.UnchangedFiles.AddRange(compareResult.UnchangedFiles.Select(n =>
                new NewItemGenerationFileInfo(
                     n,
                     Path.Combine(GenContext.Current.OutputPath, n),
                     Path.Combine(GenContext.Current.ProjectPath, n))));

            return result;
        }

        private static bool FilesAreEqual(string file, string destFilePath)
        {
            return File.ReadAllLines(file).SequenceEqual(File.ReadAllLines(destFilePath));
        }

        public void FinishGeneration(UserSelection userSelection)
        {
            try
            {
                UnsafeFinishGeneration(userSelection);
            }
            catch (Exception ex)
            {
                ShowError(ex, userSelection);
                GenContext.ToolBox.Shell.CancelWizard(false);
            }
        }

        public void UnsafeFinishGeneration(UserSelection userSelection)
        {
            var compareResult = CompareTempGenerationWithProject();
            if (userSelection.ItemGenerationType == ItemGenerationType.GenerateAndMerge)
            {
                // BackupProjectFiles
                ExecuteSyncGenerationPostActions(compareResult);
            }
            else
            {
                ExecuteOutputGenerationPostActions(compareResult);
            }
        }

        public void CleanupTempGeneration()
        {
            GenContext.Current.GenerationWarnings.Clear();
            GenContext.Current.MergeFilesFromProject.Clear();

            var directory = GenContext.Current.OutputPath;
            try
            {
                if (directory.Contains(Path.GetTempPath()))
                {
                    Fs.SafeDeleteDirectory(directory);
                }
            }
            catch (Exception ex)
            {
                var msg = $"The folder {directory} can't be delete. Error: {ex.Message}";
                AppHealth.Current.Warning.TrackAsync(msg, ex).FireAndForget();
            }
        }

        private void BackupProjectFiles(TempGenerationResult result)
        {
            var projectGuid = GenContext.ToolBox.Shell.GetActiveProjectGuid();

            if (string.IsNullOrEmpty(projectGuid))
            {
                return;
            }

            var backupFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Configuration.Current.BackupFolderPath,
                projectGuid);

            var fileName = Path.Combine(backupFolder, "backup.json");

            if (Directory.Exists(backupFolder))
            {
                Fs.SafeDeleteDirectory(backupFolder);
            }

            Fs.EnsureFolder(backupFolder);

            File.WriteAllText(fileName, JsonConvert.SerializeObject(result));

            var modifiedFiles = result.ConflictingFiles.Concat(result.ModifiedFiles);

            foreach (var file in modifiedFiles)
            {
                var originalFile = Path.Combine(GenContext.Current.ProjectPath, file);
                var backupFile = Path.Combine(backupFolder, file);
                var destDirectory = Path.GetDirectoryName(backupFile);

                Fs.SafeCopyFile(originalFile, destDirectory, true);
            }
        }

        private void ExecuteSyncGenerationPostActions(TempGenerationResult result)
        {
            var postActions = _postactionFactory.FindSyncGenerationPostActions(result);

            foreach (var postAction in postActions)
            {
                postAction.Execute();
            }
        }

        private void ExecuteOutputGenerationPostActions(TempGenerationResult result)
        {
            var postActions = _postactionFactory.FindOutputGenerationPostActions(result);

            foreach (var postAction in postActions)
            {
                postAction.Execute();
            }
        }

        private static void TrackTelemery(IEnumerable<GenInfo> genItems, Dictionary<string, TemplateCreationResult> genResults, double timeSpent, string appProjectType, string appFx)
        {
            // TODO: Include telemetry for new item
        }
    }
}
