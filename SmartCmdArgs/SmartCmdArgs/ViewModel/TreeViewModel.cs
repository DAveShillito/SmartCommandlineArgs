﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using SmartCmdArgs.Helper;
using SmartCmdArgs.View;

namespace SmartCmdArgs.ViewModel
{
    public class TreeViewModel : PropertyChangedBase
    {
        private ObservableDictionary<Guid, CmdProject> projects;
        private ObservableCollection<CmdProject> startupProjects;
        private object treeitems;
        private bool showAllProjects;

        public ObservableDictionary<Guid, CmdProject> Projects => projects;

        public object TreeItems
        {
            get => treeitems;
            private set
            {
                SetAndNotify(value, ref treeitems);
                TreeItemsView = CollectionViewSource.GetDefaultView(treeitems);
            }
        }

        private ICollectionView treeItemsView;
        public ICollectionView TreeItemsView
        {
            get => treeItemsView;
            private set => SetAndNotify(value, ref treeItemsView);
        }

        public bool ShowAllProjects
        {
            get => showAllProjects;
            set
            {
                if (showAllProjects != value)
                {
                    SetAndNotify(value, ref showAllProjects);
                    UpdateTree();
                }
            }
        }

        public CmdProject FocusedProject => projects.Values.FirstOrDefault(project => project.IsFocusedItem) ?? startupProjects.FirstOrDefault();

        public CmdBase FocusedItem => projects.Values.Concat(projects.Values.SelectMany(prj => prj)).LastOrDefault(prj => prj.IsFocusedItem) ?? startupProjects.FirstOrDefault();

        public ObservableCollection<CmdProject> StartupProjects => startupProjects;

        public List<TreeViewItemEx> DragedTreeViewItems { get; }

        public HashSet<CmdBase> SelectedItems { get; }

        public RelayCommand<int> SelectIndexCommand { get; set; }

        public RelayCommand<object> SelectItemCommand { get; set; }

        public TreeViewModel()
        {
            projects = new ObservableDictionary<Guid, CmdProject>();
            projects.CollectionChanged += OnProjectsChanged;
            treeitems = null;

            startupProjects = new ObservableCollection<CmdProject>();
            startupProjects.CollectionChanged += OnStartupProjectsChanged;

            DragedTreeViewItems = new List<TreeViewItemEx>();

            SelectedItems = new HashSet<CmdBase>();
        }

        private void OnProjectsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var cmdProject in startupProjects.Except(projects.Values).ToList())
                {
                    startupProjects.Remove(cmdProject);
                }

                foreach (var project in projects.Values)
                {
                    project.ParentTreeViewModel = this;
                }
            }
            else
            {
                var inStartupProjects = new Dictionary<Guid, CmdProject>();
                if (e.Action == NotifyCollectionChangedAction.Remove
                    || e.Action == NotifyCollectionChangedAction.Replace)
                {
                    foreach (var item in e.OldItems.Cast<KeyValuePair<Guid, CmdProject>>())
                    {
                        item.Value.ParentTreeViewModel = null;
                        if (e.Action == NotifyCollectionChangedAction.Replace && startupProjects.Contains(item.Value))
                            inStartupProjects.Add(item.Key, item.Value);
                        else if (e.Action == NotifyCollectionChangedAction.Remove)
                            startupProjects.Remove(item.Value);
                    }
                }

                if (e.Action == NotifyCollectionChangedAction.Replace
                    || e.Action == NotifyCollectionChangedAction.Add)
                {
                    foreach (var item in e.NewItems.Cast<KeyValuePair<Guid, CmdProject>>())
                    {
                        item.Value.ParentTreeViewModel = this;
                        if (e.Action == NotifyCollectionChangedAction.Replace && inStartupProjects.TryGetValue(item.Key, out CmdProject value))
                            startupProjects[startupProjects.IndexOf(value)] = item.Value;
                    }
                }
            }
        }

        private void OnStartupProjectsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var cmdProject in projects.Values)
                {
                    cmdProject.IsStartupProject = false;
                }

                foreach (var item in startupProjects)
                {
                    item.IsStartupProject = true;
                }
            }
            else
            {
                if (e.Action == NotifyCollectionChangedAction.Remove
                    || e.Action == NotifyCollectionChangedAction.Replace)
                {
                    foreach (var item in e.OldItems.Cast<CmdProject>())
                    {
                        item.IsStartupProject = false;
                    }
                }

                if (e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Replace)
                {
                    foreach (var item in e.NewItems.Cast<CmdProject>())
                    {
                        item.IsStartupProject = true;
                    }
                }
            }
            
            OnNotifyPropertyChanged(nameof(StartupProjects));
            UpdateTree();
        }

        private void UpdateTree()
        {
            if (ShowAllProjects)
            {
                TreeItems = startupProjects.Concat(projects.Values.Except(startupProjects)
                    .OrderBy(p => p.Value, StringComparer.CurrentCultureIgnoreCase)).ToList();
            }
            else
            {
                if (startupProjects.Count == 1)
                {
                    TreeItems = startupProjects.First().Items;
                }
                else
                {
                    // Fixes a strange potential bug in WPF. If ToList() is missing the project will be shown twice.
                    TreeItems = startupProjects.ToList();
                }
            }

            SelectedItems.ToList().ForEach(item => item.IsSelected = false);
            if (SelectIndexCommand?.CanExecute(0) == true)
                SelectIndexCommand.Execute(0);
        }

        public void AddItemAtFocusedItem(CmdBase item)
        {
            AddItemsAtFocusedItem(new[] {item});
        }

        public void AddItemsAtFocusedItem(IEnumerable<CmdBase> items)
        {
            var focusedItem = FocusedItem;
            if (focusedItem is CmdContainer con)
                con.Items.AddRange(items);
            else if (focusedItem is CmdArgument arg)
            {
                var insertIdx = arg.Parent.Items.IndexOf(arg) + 1;
                arg.Parent.Items.InsertRange(insertIdx, items);
            }
        }

        public void SetStringFilter(string filterString, bool matchCase = false)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                Predicate<CmdBase> filter = null;
                if (!string.IsNullOrEmpty(filterString))
                {
                    filter = item => 
                           item is CmdArgument && item.Value.Contains(filterString, matchCase ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase) 
                        || item is CmdContainer && !((CmdContainer)item).ItemsView.IsEmpty;
                }

                foreach (var project in projects.Values)
                {
                    project.Filter = filter;
                }

                TreeItemsView.Refresh();
            });
        }

        public void MoveSelectedEntries(int moveDirection)
        {
            Projects.Values.ForEach(project => project.MoveEntries(SelectedItems, moveDirection));
        }

        public void ToggleSelected()
        {
            var itemsToToggle = SelectedItems.Where(x => !SelectedItems.Contains(x.Parent)).ToList();

            var checkState = itemsToToggle.All(item => item.IsChecked == false);
            foreach (var selectedItem in itemsToToggle)
            {
                selectedItem.IsChecked = checkState;
            }
        }

        public event EventHandler<CmdBase> ItemSelectionChanged;

        public virtual void OnItemSelectionChanged(CmdBase item)
        {
            if (item.IsSelected)
                SelectedItems.Add(item);
            else
                SelectedItems.Remove(item);
            ItemSelectionChanged?.Invoke(this, item);
        }

        public ActionHistory History { get; } = new ActionHistory(10);
        public void OnContainerCollectionChanged(CmdContainer container, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                History.AddAction(new InsertAction(e.NewItems.Cast<CmdBase>(), e.NewStartingIndex, container));
            }
            if (e.Action == NotifyCollectionChangedAction.Remove)
            {
                History.AddAction(new RemoveAction(e.OldItems.Cast<CmdBase>(), e.OldStartingIndex, container));
            }
        }
    }
}
