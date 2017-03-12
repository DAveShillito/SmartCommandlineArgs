﻿//------------------------------------------------------------------------------
// <copyright file="CmdArgsPackage.cs" company="Company">
//     Copyright (c) Company.  All rights reserved.
// </copyright>
//------------------------------------------------------------------------------

using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Win32;
using System.Text;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using EnvDTE;
using SmartCmdArgs.Helper;
using SmartCmdArgs.Logic;
using SmartCmdArgs.ViewModel;

namespace SmartCmdArgs
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the
    /// IVsPackage interface and uses the registration attributes defined in the framework to
    /// register itself and its components with the shell. These attributes tell the pkgdef creation
    /// utility what data to put into .pkgdef file.
    /// </para>
    /// <para>
    /// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
    /// </para>
    /// </remarks>
    [PackageRegistration(UseManagedResourcesOnly = true)]
    [InstalledProductRegistration("#110", "#112", "1.2.3", IconResourceID = 400)] // Info on this package for Help/About
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ToolWindow), Window = ToolWindow.ToolWindowGuidString)]
    [ProvideBindingPath]
    [Guid(CmdArgsPackage.PackageGuidString)]
    [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1650:ElementDocumentationMustBeSpelledCorrectly", Justification = "pkgdef, VS and vsixmanifest are valid VS terms")]
    [ProvideOptionPage(typeof(CmdArgsOptionPage), "Smart Commandline Arguments", "General", 1000, 1001, false)]
    [ProvideKeyBindingTable(ToolWindow.ToolWindowGuidString, 200)]
    public sealed class CmdArgsPackage : Package
    {
        /// <summary>
        /// CmdArgsPackage GUID string.
        /// </summary>
        public const string PackageGuidString = "131b0c0a-5dd0-4680-b261-86ab5387b86e";
        public const string ClipboardCmdItemFormat = "SmartCommandlineArgs_D11D715E-CBF3-43F2-A1C1-168FD5C48505";
        public const string SolutionOptionKey = "SmartCommandlineArgsVA"; // Only letters are allowed

        private readonly Regex msBuildPropertyRegex = new Regex(@"\$\((?<propertyName>(?:(?!\$\()[^)])*?)\)", RegexOptions.Compiled);

        private VisualStudioHelper vsHelper;
        public ViewModel.ToolWindowViewModel ToolWindowViewModel { get; } = new ViewModel.ToolWindowViewModel();

        private bool IsVcsSupportEnabled => GetDialogPage<CmdArgsOptionPage>().VcsSupport;
        private bool IsMacroEvaluationEnabled => GetDialogPage<CmdArgsOptionPage>().MacroEvaluation;

        private ToolWindowStateSolutionData toolWindowStateLoadedFromSolution;

        private Dictionary<Project, FileSystemWatcher> projectFsWatchers = new Dictionary<Project, FileSystemWatcher>();

        /// <summary>
        /// Initializes a new instance of the <see cref="ToolWindow"/> class.
        /// </summary>
        public CmdArgsPackage()
        {
            // Inside this method you can place any initialization code that does not require
            // any Visual Studio service because at this point the package object is created but
            // not sited yet inside Visual Studio environment. The place to do all the other
            // initialization is the Initialize method.

            // add option keys to store custom data in suo file
            this.AddOptionKey(SolutionOptionKey);
        }

        #region Package Members
        internal Interface GetService<Service, Interface>()
        {
            return (Interface)base.GetService(typeof(Service));
        }

        internal Page GetDialogPage<Page>()
            where Page : class
        {
            return GetDialogPage(typeof(Page)) as Page;
        }

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        protected override void Initialize()
        {
            Commands.Initialize(this);
            base.Initialize();

            vsHelper = new VisualStudioHelper(this);
            vsHelper.SolutionOpend += VsHelper_SolutionOpend;
            vsHelper.SolutionWillClose += VsHelper_SolutionWillClose;
            vsHelper.SolutionClosed += VsHelper_SolutionClosed;
            vsHelper.StartupProjectChanged += VsHelper_StartupProjectChanged;
            vsHelper.StartupProjectConfigurationChanged += VsHelper_StartupProjectConfigurationChanged;

            vsHelper.ProjectAdded += VsHelper_ProjectAdded;
            vsHelper.ProjectRemoved += VsHelper_ProjectRemoved;
            vsHelper.ProjectRenamed += VsHelper_ProjectRenamed;

            vsHelper.Initialize();

            GetDialogPage<CmdArgsOptionPage>().MacroEvaluationChanged += OptionPage_MacroEvaluationChanged;

            // Extension window was opend while a solution is already open
            if (vsHelper.IsSolutionOpen)
            {
                Logger.Info("Package.Initialize called while solution was already open.");

                UpdateCommandsForAllProjects();
                AttachFsWatcherToAllProjects();
                UpdateCurrentStartupProject();
            }

            ToolWindowViewModel.CommandLineChanged += OnCommandLineChanged;
            ToolWindowViewModel.SelectedItemsChanged += OnSelectedItemsChanged;
        }

        private void OnCommandLineChanged(object sender, EventArgs e)
        {
            UpdateConfigurationForProject(ToolWindowViewModel.StartupProject);
        }

        private void OnSelectedItemsChanged(object sender, System.Collections.IList e)
        {
            vsHelper.UpdateShellCommandUI();
        }

        protected override WindowPane InstantiateToolWindow(Type toolWindowType)
        {
            if (toolWindowType == typeof(ToolWindow))
                return new ToolWindow(ToolWindowViewModel);
            else
                return base.InstantiateToolWindow(toolWindowType);
        }

        protected override void OnLoadOptions(string key, Stream stream)
        {
            base.OnLoadOptions(key, stream);

            if (key == SolutionOptionKey)
            {
                toolWindowStateLoadedFromSolution = Logic.ToolWindowSolutionDataSerializer.Deserialize(stream);
            }
        }

        protected override void OnSaveOptions(string key, Stream stream)
        {
            base.OnSaveOptions(key, stream);
            if (key == SolutionOptionKey)
            {
                Logger.Info("Saving all commands.");

                if (IsVcsSupportEnabled)
                {
                    Logger.Info("VcsSupport is enabled.");

                    foreach (EnvDTE.Project project in vsHelper.FindAllProjects())
                    {
                        ViewModel.ListViewModel vm = null;
                        if (ToolWindowViewModel.SolutionArguments.TryGetValue(project, out vm))
                        {
                            string filePath = FullFilenameForProjectJsonFile(project);
                            FileSystemWatcher fsWatcher = projectFsWatchers.GetValueOrDefault(project);

                            if (vm.DataCollection.Count != 0)
                            {
                                using (fsWatcher?.TemporarilyDisable())
                                {
                                    try
                                    {
                                        using (Stream fileStream = File.Open(filePath, FileMode.Create, FileAccess.Write))
                                        {
                                            Logic.ToolWindowProjectDataSerializer.Serialize(vm, fileStream);
                                        }
                                    }
                                    catch (Exception e)
                                    {
                                        Logger.Warn($"Failed to write to file '{filePath}' with error '{e}'.");
                                    }
                                }
                            }
                            else if (File.Exists(filePath))
                            {
                                Logger.Info("Deleting json file because command list is empty but json-file exists.");

                                try
                                {
                                    File.Delete(filePath);
                                }
                                catch (Exception e)
                                {
                                    Logger.Warn($"Failed to delete file '{filePath}' with error '{e}'.");
                                }
                            }
                        }
                    }
                }

                toolWindowStateLoadedFromSolution = ToolWindowSolutionDataSerializer.Serialize(ToolWindowViewModel, stream);
            }
            Logger.Info("All Commands Saved.");
        }

        #endregion

        private void UpdateConfigurationForProject(Project project)
        {
            if (project == null) return;
            IEnumerable<string> enabledEntries;
            if (IsMacroEvaluationEnabled)
            {
                enabledEntries = ToolWindowViewModel.EnabledItemsForCurrentProject().Select(
                    e => msBuildPropertyRegex.Replace(e.Command,
                        match => vsHelper.GetMSBuildPropertyValue(project, match.Groups["propertyName"].Value) ?? match.Value));
            }
            else
            {
                enabledEntries = ToolWindowViewModel.EnabledItemsForCurrentProject().Select(e => e.Command);
            }
            string prjCmdArgs = string.Join(" ", enabledEntries);
            Helper.ProjectArguments.SetArguments(project, prjCmdArgs);
            Logger.Info($"Updated Configuration for Project: {project.UniqueName}");
        }

        private void AttachFsWatcherToProject(Project project)
        {
            string realProjectJsonFileFullName = SymbolicLinkUtils.GetRealPath(FullFilenameForProjectJsonFile(project));
            try
            {
                var projectJsonFileWatcher = new FileSystemWatcher();

                projectJsonFileWatcher.Path = Path.GetDirectoryName(realProjectJsonFileFullName);
                projectJsonFileWatcher.Filter = Path.GetFileName(realProjectJsonFileFullName);

                projectJsonFileWatcher.EnableRaisingEvents = true;
                projectFsWatchers.Add(project, projectJsonFileWatcher);

                projectJsonFileWatcher.Changed += (fsWatcher, args) => { if (IsVcsSupportEnabled) UpdateCommandsForProjectOnDispatcher(project); };
                projectJsonFileWatcher.Created += (fsWatcher, args) => { if (IsVcsSupportEnabled) UpdateCommandsForProjectOnDispatcher(project); };
                projectJsonFileWatcher.Renamed += (fsWatcher, args) =>
                    { if (IsVcsSupportEnabled && realProjectJsonFileFullName == args.FullPath) UpdateCommandsForProjectOnDispatcher(project); };

                Logger.Info($"Attached FileSystemWatcher to file '{realProjectJsonFileFullName}' for project '{project.UniqueName}'.");
            }
            catch (Exception e)
            {
                Logger.Warn($"Failed to attach FileSystemWatcher to file '{realProjectJsonFileFullName}' for project '{project.UniqueName}' with error '{e}'.");
            }
        }

        private void AttachFsWatcherToAllProjects()
        {
            foreach (var project in vsHelper.FindAllProjects())
            {
                AttachFsWatcherToProject(project);
            }
        }

        private void DetachFsWatcherFromProject(Project project)
        {
            FileSystemWatcher fsWatcher;
            if (projectFsWatchers.TryGetValue(project, out fsWatcher))
            {
                fsWatcher.Dispose();
                projectFsWatchers.Remove(project);
                Logger.Info($"Detached FileSystemWatcher for project '{project.UniqueName}'.");
            }
        }

        private void DetachFsWatcherFromAllProjects()
        {
            foreach (var pair in projectFsWatchers)
            {
                pair.Value.Dispose();
            }
            projectFsWatchers.Clear();
            Logger.Info("Detached FileSystemWatcher for all projects");
        }

        private void UpdateCommandsForProjectOnDispatcher(Project project)
        {
            Application.Current.Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(() =>
                {
                    UpdateCommandsForProject(project);
                }));
        }

        private void UpdateCommandsForProject(Project project)
        {
            if (project == null)
                throw new ArgumentNullException(nameof(project));

            Logger.Info($"Update commands for project '{project.UniqueName}'. IsVcsSupportEnabled={IsVcsSupportEnabled}. SolutionData.Count={toolWindowStateLoadedFromSolution?.Count}.");

            var solutionData = toolWindowStateLoadedFromSolution ?? new ToolWindowStateSolutionData();

            // joins data from solution and project
            //  => overrides solution commands for a project if a project json file exists
            //  => keeps all data from the suo file for projects without a json
            //  => if we have data in our ViewModel we use this instad of the suo file

            // get project json data
            ToolWindowStateProjectData projectData = null;
            if (IsVcsSupportEnabled)
            {
                string filePath = FullFilenameForProjectJsonFile(project);

                if (File.Exists(filePath))
                {
                    try
                    {
                        using (Stream fileStream = File.Open(filePath, FileMode.Open, FileAccess.Read))
                        {
                            projectData = Logic.ToolWindowProjectDataSerializer.Deserialize(fileStream);
                        }
                        Logger.Info($"Read {projectData?.DataCollection?.Count} commands for project '{project.UniqueName}' from json-file '{filePath}'.");
                    }
                    catch (Exception e)
                    {
                        Logger.Warn($"Failed to read file '{filePath}' with error '{e}'.");
                        projectData = null;
                    }
                }
                else
                {
                    Logger.Info($"Json-file '{filePath}' doesn't exists.");
                }
            }

            // project json overrides if it exists
            if (projectData != null)
            {
                Logger.Info($"Setting {projectData?.DataCollection?.Count} commands for project '{project.UniqueName}' from json-file.");

                ToolWindowStateProjectData curSolutionProjectData = solutionData.GetValueOrDefault(project.UniqueName);
                ListViewModel projectListViewModel = ToolWindowViewModel.SolutionArguments.GetValueOrDefault(project);

                // check if we have data in the suo file or the ViewModel
                if (curSolutionProjectData != null || projectListViewModel != null)
                {
                    // update enabled state of the project json data (source prio: ViewModel > suo file)
                    var dataCollectionFromProject = projectData?.DataCollection;
                    if (dataCollectionFromProject != null)
                    {
                        var dataCollectionFromSolution = curSolutionProjectData?.DataCollection;
                        var dataCollectionFromLVM = projectListViewModel?.DataCollection;
                        foreach (var dataFromProject in dataCollectionFromProject)
                        {
                            var dataFromVM = dataCollectionFromLVM?.FirstOrDefault(data => data.Id == dataFromProject.Id);

                            if (dataFromVM != null)
                                dataFromProject.Enabled = dataFromVM.Enabled;
                            else
                            {
                                var dataFromSolution =
                                    dataCollectionFromSolution?.Find(data => data.Id == dataFromProject.Id);

                                if (dataFromSolution != null)
                                    dataFromProject.Enabled = dataFromSolution.Enabled;
                            }
                        }
                    }
                    else
                    {
                        projectData = new ToolWindowStateProjectData();
                        Logger.Info($"DataCollection for project '{project.UniqueName}' is null.");
                    }
                }
            }
            // if we have data in the ViewModel we keep it
            else if (ToolWindowViewModel.SolutionArguments.ContainsKey(project))
            {
                return;
            }
            // we try to read the suo file data
            else if (!solutionData.TryGetValue(project.UniqueName, out projectData))
            {
                Logger.Info($"Gathering commands from configurations for project '{project.UniqueName}'.");
                // if we don't have suo file data we read cmd args from the project configs
                projectData = new ToolWindowStateProjectData();
                projectData.DataCollection.AddRange(
                    ReadCommandlineArgumentsFromProject(project)
                        .Select(cmdLineArg => new ToolWindowStateProjectData.ListEntryData {Command = cmdLineArg}));
            }
            else if (IsVcsSupportEnabled)
            {
                projectData = new ToolWindowStateProjectData();
                Logger.Info("Will clear all data because of missing json file and enabled VCS support.");
            }
            else
            {
                Logger.Info($"Will use commands from suo file for project '{project.UniqueName}'.");
            }

            // push projectData to the ViewModel
            ToolWindowViewModel.PopulateFromProjectData(project, projectData);
            if (project == ToolWindowViewModel.StartupProject)
                UpdateConfigurationForProject(project);

            Logger.Info($"Updated Commands for project '{project.UniqueName}'.");
        }

        private void UpdateCommandsForAllProjects()
        {
            foreach (var project in vsHelper.FindAllProjects())
            {
                UpdateCommandsForProject(project);
            }
        }

        #region VS Events
        private void VsHelper_SolutionOpend(object sender, EventArgs e)
        {
            Logger.Info("VS-Event: Solution opened.");

            foreach (var project in vsHelper.FindAllProjects())
            {
                UpdateCommandsForProject(project);
                AttachFsWatcherToProject(project);
            }

            UpdateCurrentStartupProject();
        }

        private void VsHelper_SolutionWillClose(object sender, EventArgs e)
        {
            Logger.Info("VS-Event: Solution will close.");

            DetachFsWatcherFromAllProjects();
        }

        private void VsHelper_SolutionClosed(object sender, EventArgs e)
        {
            Logger.Info("VS-Event: Solution closed.");

            ToolWindowViewModel.Reset();
            toolWindowStateLoadedFromSolution = null;
        }

        private void VsHelper_StartupProjectChanged(object sender, EventArgs e)
        {
            Logger.Info("VS-Event: startup project changed.");

            UpdateCurrentStartupProject();
        }

        private void VsHelper_StartupProjectConfigurationChanged(object sender, EventArgs e)
        {
            Logger.Info("VS-Event: Startup project configuration changed.");

            UpdateConfigurationForProject(ToolWindowViewModel.StartupProject);
        }

        private void VsHelper_ProjectAdded(object sender, Project project)
        {
            Logger.Info($"VS-Event: Project added. IsSolutionOpen={vsHelper.IsSolutionOpen}");

            if (vsHelper.IsSolutionOpen)
            {
                UpdateCommandsForProject(project);
                AttachFsWatcherToProject(project);
            }
        }

        private void VsHelper_ProjectRemoved(object sender, Project project)
        {
            Logger.Info("VS-Event: Project removed.");

            if (ToolWindowViewModel.StartupProject == project)
                ToolWindowViewModel.UpdateStartupProject(null);

            ToolWindowViewModel.SolutionArguments.Remove(project);
            DetachFsWatcherFromProject(project);
        }

        private void VsHelper_ProjectRenamed(object sender, VisualStudioHelper.ProjectRenamedEventArgs e)
        {
            Logger.Info("VS-Event: Project renamed.");

            FileSystemWatcher fsWatcher;
            if (projectFsWatchers.TryGetValue(e.project, out fsWatcher))
            {
                using (fsWatcher.TemporarilyDisable())
                {
                    var newFileName = FullFilenameForProjectJsonFile(e.project);
                    var oldFileName = FullFilenameForProjectJsonFile(e.oldName);

                    Logger.Info($"Renaming json-file '{oldFileName}' to new name '{newFileName}'");

                    if (File.Exists(newFileName))
                    {
                        File.Delete(oldFileName);
                        UpdateCommandsForProject(e.project);
                    }
                    else if (File.Exists(oldFileName))
                    {
                        File.Move(oldFileName, newFileName);
                    }
                    fsWatcher.Filter = Path.GetFileName(newFileName);
                }
            }
        }
        #endregion

        #region OptionPage Events
        private void OptionPage_MacroEvaluationChanged(object sender, bool newVlaue)
        {
            UpdateCommandsForAllProjects();
        }
        #endregion

        private IEnumerable<string> ReadCommandlineArgumentsFromProject(Project project)
        {
            List<string> prjCmdArgs = new List<string>();
            Helper.ProjectArguments.AddAllArguments(project, prjCmdArgs);
            return prjCmdArgs.Distinct();
        }

        private void UpdateCurrentStartupProject()
        {
            Project startupProject;
            vsHelper.FindStartupProject(out startupProject);

            // update StartupProject and if it changed update the Configuration
            if (ToolWindowViewModel.UpdateStartupProject(startupProject))
                UpdateConfigurationForProject(startupProject);
        }


        private string FullFilenameForProjectJsonFile(EnvDTE.Project project)
        {
            var userFilename = vsHelper.GetMSBuildPropertyValue(project, "SmartCmdArgJsonFile");
            
            if (!string.IsNullOrEmpty(userFilename))
            {
                // It's recommended to use absolute paths for the json file in the first place...
                userFilename = Path.GetFullPath(userFilename); // ... but make it absolute in any case.
                
                Logger.Info($"'SmartCmdArgJsonFile' msbuild property present in project '{project.UniqueName}' will use json file '{userFilename}'.");
                return userFilename;
            }
            else
            {
                return FullFilenameForProjectJsonFile(project.FullName);
            }
        }

        private string FullFilenameForProjectJsonFile(string projectFile)
        {
            string filename = $"{Path.GetFileNameWithoutExtension(projectFile)}.args.json";
            return Path.Combine(Path.GetDirectoryName(projectFile), filename);
        }
    }
}
