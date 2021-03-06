using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using PluginCore;
using PluginCore.Managers;
using ProjectManager.Projects;
using ProjectManager.Projects.AS3;
using ProjectManager.Controls.AS3;
using System.Runtime.InteropServices;

namespace ProjectManager.Controls.TreeView
{
    public delegate void DirectoryNodeRefresh(DirectoryNode node);
    public delegate void DirectoryNodeMapping(DirectoryNode node, FileMappingRequest request);

    public class PlaceholderNode : GenericNode
    {
        public PlaceholderNode(string backingPath) : base(backingPath) { }
    }

    public class DirectoryNode : GenericNode
	{
        static public event DirectoryNodeRefresh OnDirectoryNodeRefresh;
        static public event DirectoryNodeMapping OnDirectoryNodeMapping;

		bool dirty;
        DirectoryNode insideClasspath;

        public DirectoryNode InsideClasspath { get { return insideClasspath; } }

		public DirectoryNode(string directory) : base(directory)
		{
			isDraggable = true;
			isDropTarget = true;
			isRenamable = true;
		}

		public override void Dispose()
		{
			base.Dispose();
			// dispose children
			foreach (GenericNode node in Nodes)
                node.Dispose();
		}

        [DllImport("shlwapi.dll", CharSet = CharSet.Auto)]
        static extern bool PathIsDirectoryEmpty([In] string lpszPath);

		public override void Refresh(bool recursive)
		{
			if (IsInvalid) return;

			base.Refresh(recursive);
            Project project = MyProject;

            // item icon
            if (Parent is DirectoryNode) 
                insideClasspath = (Parent as DirectoryNode).insideClasspath;

            if (project.IsPathHidden(BackingPath))
                ImageIndex = Icons.HiddenFolder.Index;
            else if (insideClasspath == null && project.IsClassPath(BackingPath))
            {
                insideClasspath = this;
                ImageIndex = Icons.ClasspathFolder.Index;
            }
            else if (insideClasspath != null && project.IsCompileTarget(BackingPath))
                ImageIndex = Icons.FolderCompile.Index;
            else
                ImageIndex = Icons.Folder.Index;

            SelectedImageIndex = ImageIndex;

            Color color = PluginCore.PluginBase.MainForm.GetThemeColor("ProjectTreeView.ForeColor");
            if (color != Color.Empty) ForeColorRequest = color;
            else ForeColorRequest = SystemColors.ControlText;

			// make the plus/minus sign correct
            bool empty = !Directory.Exists(BackingPath) || PathIsDirectoryEmpty(BackingPath);

			if (!empty)
			{
				// we want the plus sign because we have *something* in here
				if (Nodes.Count == 0)
					Nodes.Add(new PlaceholderNode(Path.Combine(BackingPath, "placeholder")));

				// we're already expanded, so refresh our children
				if (IsExpanded || Path.GetDirectoryName(Tree.PathToSelect) == BackingPath)
					PopulateChildNodes(recursive, project);
				else
					dirty = true; // refresh on demand
			}
			else
			{
				// we just became empty!
				if (Nodes.Count > 0)
					PopulateChildNodes(recursive, project);
			}

            NotifyRefresh();
		}

        protected void NotifyRefresh()
        {
            // hook for plugins
            if (OnDirectoryNodeRefresh != null) OnDirectoryNodeRefresh(this);
        }

		/// <summary>
		/// Signal this node that it is about to expand.
		/// </summary>
		public override void BeforeExpand()
		{
			if (dirty) PopulateChildNodes(false, MyProject);
		}

		private void PopulateChildNodes(bool recursive, Project project)
		{
			dirty = false;

			// nuke the placeholder
			if (Nodes.Count == 1 && Nodes[0] is PlaceholderNode)
				Nodes.RemoveAt(0);

			// do a nice stateful update against the filesystem
            GenericNodeList nodesToDie = new GenericNodeList();
			
			// don't remove project output node if it exists - it's annoying when it
			// disappears during a build
            foreach (GenericNode node in Nodes)
            {
                if (node is ProjectOutputNode)
                {
                    var output = node as ProjectOutputNode;
                    if (!project.IsPathHidden(output.BackingPath)) output.Refresh(recursive);
                    else nodesToDie.Add(output);
                }
                else if (node is ReferencesNode) node.Refresh(recursive);
                else nodesToDie.Add(node);

                // add any mapped nodes
                if (node is FileNode && !(node is SwfFileNode))
                    nodesToDie.AddRange(node.Nodes);
            }

            if (Directory.Exists(BackingPath))
            {
                PopulateDirectories(nodesToDie, recursive, project);
                PopulateFiles(nodesToDie, recursive, project);
            }

			foreach (GenericNode node in nodesToDie)
			{
                node.Dispose();
				Nodes.Remove(node);
			}
		}

        private void PopulateDirectories(GenericNodeList nodesToDie, bool recursive, Project project)
		{
			foreach (string directory in Directory.GetDirectories(BackingPath))
			{
				if (IsDirectoryExcluded(directory, project))
					continue;

                DirectoryNode node;
				if (Tree.NodeMap.ContainsKey(directory))
				{
					node = Tree.NodeMap[directory] as DirectoryNode;
                    if (node != null) // ASClassWizard injects SimpleDirectoryNode != DirectoryNode
                    {
                        if (recursive) node.Refresh(recursive);
                        nodesToDie.Remove(node);
                        continue;
                    }
                    else 
                    {
                        TreeNode fake = Tree.NodeMap[directory];
                        if (fake.Parent != null) fake.Parent.Nodes.Remove(fake);
                    }
				}

				node = new DirectoryNode(directory);
				InsertNode(Nodes, node);
				node.Refresh(recursive);
				nodesToDie.Remove(node);
			}
		}

        private void PopulateFiles(GenericNodeList nodesToDie, bool recursive, Project project)
		{
            string[] files = Directory.GetFiles(BackingPath);

			foreach (string file in files)
			{
				if (IsFileExcluded(file, project))
					continue;

				if (Tree.NodeMap.ContainsKey(file))
				{
                    GenericNode node = Tree.NodeMap[file];
					node.Refresh(recursive);
					nodesToDie.Remove(node);
				}
				else
				{
					FileNode node = FileNode.Create(file);
					InsertNode(Nodes, node);
					node.Refresh(recursive);
					nodesToDie.Remove(node);
				}
			}

            FileMapping mapping = GetFileMapping(files);

            foreach (string file in files)
            {
                if (IsFileExcluded(file, project))
                    continue;

                GenericNode node = Tree.NodeMap[file];

                // ensure this file is in the right spot
                if (mapping != null && mapping.ContainsKey(file) && Tree.NodeMap.ContainsKey(mapping[file]))
                    EnsureParentedBy(node, Tree.NodeMap[mapping[file]]);
                else
                    EnsureParentedBy(node, this);
            }
		}

        private void EnsureParentedBy(GenericNode child, GenericNode parent)
        {
            if (child.Parent != parent)
            {
                child.Parent.Nodes.Remove(child);
                InsertNode(parent.Nodes, child);
            }
        }

        // Let another plugin extend the tree by specifying mapping
        private FileMapping GetFileMapping(string[] files)
        {
            FileMappingRequest request = new FileMappingRequest(files);

            // Give plugins a chance to respond first
            if (OnDirectoryNodeMapping != null) OnDirectoryNodeMapping(this, request);

            // No one cares?  ok, well we do know one thing: Mxml
            if (request.Mapping.Count == 0 && Tree.Project is AS3Project 
                && PluginMain.Settings.EnableMxmlMapping)
                MxmlFileMapping.AddMxmlMapping(request);

            return request.Mapping.Count > 0 ? request.Mapping : null;
        }

		/// <summary>
		/// Inserts a node in the correct location (sorting alphabetically by
		/// directories first, then files).
		/// </summary>
		/// <param name="node"></param>
		private static void InsertNode(TreeNodeCollection nodes, GenericNode node)
		{
			bool inserted = false;

			for (int i=0; i<nodes.Count; i++)
			{
                GenericNode existingNode = nodes[i] as GenericNode;

				if (node is FileNode && existingNode is DirectoryNode)
					continue;

				if ((node is DirectoryNode && existingNode is FileNode)
                    || string.Compare(existingNode.Text, node.Text, true) > 0)
				{
					nodes.Insert(i,node);
					inserted = true;
					break;
				}
			}

			if (!inserted)
				nodes.Add(node); // append to the end of the list

			// is the tree looking to have this node selected?
			if (Tree.PathToSelect == node.BackingPath)
			{
				// use SelectedNode so multiselect treeview can handle painting
				Tree.SelectedNodes = new ArrayList(new object[]{node});
			}
		}

		bool IsDirectoryExcluded(string path, Project project)
		{
			string dirName = Path.GetFileName(path);
			foreach (string excludedDir in PluginMain.Settings.ExcludedDirectories)
				if (dirName.Equals(excludedDir, StringComparison.OrdinalIgnoreCase))
					return true;

            return !project.ShowHiddenPaths && project.IsPathHidden(path);
		}

		bool IsFileExcluded(string path, Project project)
		{
			if (path == project.ProjectPath) return true;

            return !project.ShowHiddenPaths && (project.IsPathHidden(path) || path.IndexOf("\\.") >= 0 || ProjectTreeView.IsFileTypeHidden(path));
		}
	}
}
