﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using EnvDTE;
using Microsoft.VisualStudio.Shell.Interop;
using SmartCmdArgs.Helper;
using SmartCmdArgs.Logic;
using JsonConvert = Newtonsoft.Json.JsonConvert;

namespace SmartCmdArgs.ViewModel
{
    public class ToolWindowViewModel : PropertyChangedBase
    {
        public static readonly string DefaultFontFamily = null;
        public static readonly string MonospaceFontFamily = "Consolas";
        
        public TreeViewModel TreeViewModel { get; }

        private bool _isInEditMode;
        public bool IsInEditMode
        {
            get { return _isInEditMode; }
            set { _isInEditMode = value; OnNotifyPropertyChanged(); }
        }

        private string _usedFontFamily;
        public string UsedFontFamily
        {
            get => _usedFontFamily;
            private set { _usedFontFamily = value; OnNotifyPropertyChanged(); }
        }

        private bool _useMonospaceFont;
        public bool UseMonospaceFont
        {
            get => _useMonospaceFont;
            set
            {
                if (value == _useMonospaceFont)
                    return;

                if (value)
                    UsedFontFamily = MonospaceFontFamily;
                else
                    UsedFontFamily = DefaultFontFamily;
                _useMonospaceFont = value;
            }
        }

        public RelayCommand AddEntryCommand { get; }

        public RelayCommand AddGroupCommand { get; }

        public RelayCommand RemoveEntriesCommand { get; }

        public RelayCommand MoveEntriesUpCommand { get; }

        public RelayCommand MoveEntriesDownCommand { get; }

        public RelayCommand CopyCommandlineCommand { get; }

        public RelayCommand ShowAllProjectsCommand { get; }

        public RelayCommand ToggleSelectedCommand { get; }
        
        public RelayCommand CopySelectedItemsCommand { get; }
        
        public RelayCommand PasteItemsCommand { get; }
        
        public RelayCommand CutItemsCommand { get; }

        public RelayCommand UndoCommand { get; }

        public RelayCommand RedoCommand { get; }

        public event EventHandler CommandLineChanged;

        public ToolWindowViewModel()
        {
            TreeViewModel = new TreeViewModel();

            AddEntryCommand = new RelayCommand(
                () => {
                    var newArg = new CmdArgument(arg: "", isChecked: true);
                    TreeViewModel.AddItemAtFocusedItem(newArg);
                    if (TreeViewModel.SelectItemCommand.CanExecute(newArg))
                        TreeViewModel.SelectItemCommand.Execute(newArg);
                }, canExecute: _ => HasStartupProject());

            AddGroupCommand = new RelayCommand(
                () => {
                    var newGrp = new CmdGroup(name: "");
                    TreeViewModel.AddItemAtFocusedItem(newGrp);
                    if (TreeViewModel.SelectItemCommand.CanExecute(newGrp))
                        TreeViewModel.SelectItemCommand.Execute(newGrp);
                }, canExecute: _ => HasStartupProject());

            RemoveEntriesCommand = new RelayCommand(
                () => {
                    RemoveSelectedItems();
                }, canExecute: _ => HasStartupProjectAndSelectedItems());

            MoveEntriesUpCommand = new RelayCommand(
                () => {
                    TreeViewModel.MoveSelectedEntries(moveDirection: -1);
                }, canExecute: _ => HasStartupProjectAndSelectedItems());

            MoveEntriesDownCommand = new RelayCommand(
                () => {
                    TreeViewModel.MoveSelectedEntries(moveDirection: 1);
                }, canExecute: _ => HasStartupProjectAndSelectedItems());

            CopyCommandlineCommand = new RelayCommand(
                () => {
                    IEnumerable<string> enabledEntries;
                    enabledEntries = TreeViewModel.FocusedProject.CheckedArguments.Select(e => e.Value);
                    string prjCmdArgs = string.Join(" ", enabledEntries);
                    Clipboard.SetText(prjCmdArgs);
                }, canExecute: _ => HasStartupProject());

            ShowAllProjectsCommand = new RelayCommand(
                () => {
                    TreeViewModel.ShowAllProjects = !TreeViewModel.ShowAllProjects;
                });

            ToggleSelectedCommand = new RelayCommand(
                () => {
                    TreeViewModel.ToggleSelected();
                }, canExecute: _ => HasStartupProject());

            UndoCommand = new RelayCommand(() => TreeViewModel.History.Undo());
            
            RedoCommand = new RelayCommand(() => TreeViewModel.History.Redo());

            CopySelectedItemsCommand = new RelayCommand(() => CopySelectedItemsToClipboard(includeProjects: true), canExecute: _ => HasSelectedItems());

            PasteItemsCommand = new RelayCommand(PasteItemsFromClipboard, canExecute: _ => HasStartupProject());

            CutItemsCommand = new RelayCommand(CutItemsToClipboard, canExecute: _ => HasSelectedItems());

            TreeViewModel.Projects.ItemPropertyChanged += OnArgumentListItemChanged;
            TreeViewModel.Projects.CollectionChanged += OnArgumentListChanged;
        }

        /// <summary>
        /// Resets the whole state of the tool window view model
        /// </summary>
        public void Reset()
        {
            TreeViewModel.Projects.Clear();
        }

        private void CopySelectedItemsToClipboard(bool includeProjects)
        {
            var selectedItems = TreeViewModel.Projects.Values.SelectMany(prj => prj.GetEnumerable(includeSelf: includeProjects)).Where(item => item.IsSelected).ToList();
            var set = new HashSet<CmdContainer>(selectedItems.OfType<CmdContainer>());
            var itemsToCopy = selectedItems.Where(x => !set.Contains(x.Parent));

            if (includeProjects)
                itemsToCopy = itemsToCopy.SelectMany(item => item is CmdProject prj ? prj.Items : Enumerable.Repeat(item, 1));

            var itemListToCopy = itemsToCopy.ToList();
            if (itemListToCopy.Count > 0)
                Clipboard.SetDataObject(DataObjectGenerator.Genrate(itemListToCopy, includeObject: false));
        }

        private void PasteItemsFromClipboard()
        {
            var pastedItems = DataObjectGenerator.Extract(Clipboard.GetDataObject(), includeObject: false)?.ToList();
            if (pastedItems != null && pastedItems.Count > 0)
            {
                TreeViewModel.AddItemsAtFocusedItem(pastedItems);
                TreeViewModel.SelectItemCommand.Execute(pastedItems.First());
                foreach (var pastedItem in pastedItems.Skip(1))
                {
                    pastedItem.IsSelected = true;
                }
            }
        }

        private void CutItemsToClipboard()
        {
            CopySelectedItemsToClipboard(includeProjects: false);
            RemoveSelectedItems();
        }


        /// <summary>
        /// Helper method for CanExecute condition of the commands
        /// </summary>
        /// <returns>True if a valid startup project is set</returns>
        private bool HasStartupProject()
        {
            return TreeViewModel.StartupProjects.Any();
        }

        /// <summary>
        /// Helper method for CanExecute condition of the commands
        /// </summary>
        /// <returns>True if any line is selected</returns>
        private bool HasSelectedItems()
        {
            return TreeViewModel.SelectedItems.Count > 0;
        }

        /// <summary>
        /// Helper method for CanExecute condition of the commands
        /// </summary>
        /// <returns>True if a valid startup project is set and any line is selected</returns>
        private bool HasStartupProjectAndSelectedItems()
        {
            return HasStartupProject() && HasSelectedItems();
        }

        private void RemoveSelectedItems()
        {
            var indexToSelect = TreeViewModel.TreeItemsView.OfType<CmdBase>()
                .SelectMany(item => item is CmdContainer con ? con.GetEnumerable(true, true, false) : Enumerable.Repeat(item, 1))
                .TakeWhile(item => !item.IsSelected).Count();

            bool removedAnItem = false;
            using (TreeViewModel.History.OpenGroup())
            {
                foreach (var item in TreeViewModel.SelectedItems.ToList())
                {
                    if (item.Parent != null)
                    {
                        item.Parent.Items.Remove(item);
                        TreeViewModel.SelectedItems.Remove(item);
                        removedAnItem = true;
                    }
                }
            }
            if (!removedAnItem)
                return;

            indexToSelect = TreeViewModel.TreeItemsView.OfType<CmdBase>()
                .SelectMany(item => item is CmdContainer con ? con.GetEnumerable(true, true, false) : Enumerable.Repeat(item, 1))
                .Take(indexToSelect + 1).Count() - 1;
            if (TreeViewModel.SelectIndexCommand.CanExecute(indexToSelect))
                TreeViewModel.SelectIndexCommand.Execute(indexToSelect);
        }

        public void PopulateFromProjectData(IVsHierarchy project, ToolWindowStateProjectData data)
        {
            var guid = project.GetGuid();
            TreeViewModel.Projects[guid] = new CmdProject(guid, project.GetKind(), project.GetDisplayName(), ListEntriesToCmdObjects(data.Items));

            IEnumerable<CmdBase> ListEntriesToCmdObjects(List<ListEntryData> list)
            {
                foreach (var item in list)
                {
                    if (item.Items == null)
                        yield return new CmdArgument(item.Id, item.Command, item.Enabled);
                    else
                        yield return new CmdGroup(item.Id, item.Command, ListEntriesToCmdObjects(item.Items), item.Expanded);
                }
            }
        }

        public void RenameProject(IVsHierarchy project)
        {
            if (TreeViewModel.Projects.TryGetValue(project.GetGuid(), out CmdProject cmdProject))
            {
                cmdProject.Value = project.GetDisplayName();
            }
        }

        public void CancelEdit()
        {
            System.Windows.Controls.DataGrid.CancelEditCommand.Execute(null, null);
        }

        private void OnArgumentListItemChanged(object sender, CollectionItemPropertyChangedEventArgs<CmdProject> args)
        {
            OnCommandLineChanged();
        }

        private void OnArgumentListChanged(object sender, NotifyCollectionChangedEventArgs args)
        {
            OnCommandLineChanged();
        }

        protected virtual void OnCommandLineChanged()
        {
            CommandLineChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
