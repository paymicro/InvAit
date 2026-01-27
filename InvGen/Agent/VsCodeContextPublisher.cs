using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using InvGen.Utils;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Wpf;
using Shared.Contracts;

namespace InvGen.Agent
{
    public sealed class VsCodeContextPublisher : IDisposable
    {
        private readonly DTE2 _dte;
        private readonly WebView2 _webView;
        private readonly SolutionEvents _solutionEvents;
        private readonly WindowEvents _windowEvents;
        private readonly DocumentEvents _documentEvents;

        private VsCodeContext _currentContext = new VsCodeContext();
        private DateTime _lastSolutionUpdateTime = DateTime.MinValue;
        private readonly SemaphoreSlim _updateSemaphore = new SemaphoreSlim(1, 1);
        private const int SolutionUpdateThrottleMs = 10000;
        private bool _isDisposed;


        private VsCodeContextPublisher(DTE2 dte, WebView2 webView)
        {
            _dte = dte;
            _webView = webView;

            _solutionEvents = _dte.Events.SolutionEvents;
            _windowEvents = _dte.Events.WindowEvents;
            _documentEvents = _dte.Events.DocumentEvents;
        }

        public static async Task<VsCodeContextPublisher> CreateAsync(DTE2 dte, WebView2 webView)
        {
            var publisher = new VsCodeContextPublisher(dte, webView);
            await publisher.InitializeAsync();
            return publisher;
        }

        private async Task InitializeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _solutionEvents.Opened += SolutionChangedHandler;
            _solutionEvents.AfterClosing += SolutionChangedHandler;
            _solutionEvents.ProjectAdded += SolutionProjectChangedHandler;
            _solutionEvents.ProjectRemoved += SolutionProjectChangedHandler;
            _solutionEvents.ProjectRenamed += SolutionProjectRenamedHandler;

            _windowEvents.WindowActivated += WindowActivatedHandler;
            _documentEvents.DocumentSaved += DocumentSavedHandler;
            
            await UpdateContextAsync(true, true);
        }

        private void DocumentSavedHandler(Document document) => UpdateContextAsync(true, false).FireAndForget();
        private void WindowActivatedHandler(Window gotFocus, Window lostFocus) => UpdateContextAsync(false, true).FireAndForget();
        private void SolutionProjectRenamedHandler(Project project, string oldName) => UpdateContextAsync(true, false).FireAndForget();
        private void SolutionProjectChangedHandler(Project project) => UpdateContextAsync(true, false).FireAndForget();
        private void SolutionChangedHandler() => UpdateContextAsync(true, true).FireAndForget();

        private async Task UpdateContextAsync(bool updateSolution, bool updateActiveDocument)
        {
            if (_isDisposed) return;

            await _updateSemaphore.WaitAsync();
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (updateSolution)
                {
                    if ((DateTime.UtcNow - _lastSolutionUpdateTime).TotalMilliseconds > SolutionUpdateThrottleMs)
                    {
                        UpdateSolutionStructure();
                        _lastSolutionUpdateTime = DateTime.UtcNow;
                    }
                }

                if (updateActiveDocument)
                {
                    UpdateActiveDocumentAndSelection();
                }
                
                await SendContextUpdateAsync();
            }
            finally
            {
                _updateSemaphore.Release();
            }
        }

        private void UpdateActiveDocumentAndSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (_dte.ActiveDocument != null)
                {
                    _currentContext.ActiveDocument = _dte.ActiveDocument.FullName;
                    if (_dte.ActiveDocument.Selection is TextSelection selection)
                    {
                        _currentContext.Selection = (selection.TopLine, selection.BottomLine);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error getting active document or selection: {ex.Message}", "ERROR");
            }
        }

        private void UpdateSolutionStructure()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (_dte.Solution != null && _dte.Solution.IsOpen)
                {
                    var solutionNode = new SolutionNode
                    {
                        Name = Path.GetFileName(_dte.Solution.FullName),
                        Path = _dte.Solution.FullName,
                        Type = "solution"
                    };

                    foreach (Project project in _dte.Solution.Projects)
                    {
                        solutionNode.Children.Add(GetProjectNode(project));
                    }
                    _currentContext.Solution = solutionNode;
                }
                else
                {
                    _currentContext.Solution = null;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error building solution structure: {ex.Message}", "ERROR");
            }
        }

        private SolutionNode GetProjectNode(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var projectNode = new SolutionNode
            {
                Name = project.Name,
                Path = project.FullName,
                Type = "project"
            };

            if (project.ProjectItems != null)
            {
                foreach (ProjectItem item in project.ProjectItems)
                {
                    projectNode.Children.Add(GetProjectItemNode(item));
                }
            }
            return projectNode;
        }

        private SolutionNode GetProjectItemNode(ProjectItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var itemNode = new SolutionNode
            {
                Name = item.Name,
                Path = item.FileCount > 0 ? item.FileNames[0] : null,
                Type = GetProjectItemType(item)
            };

            if (item.ProjectItems != null)
            {
                foreach (ProjectItem subItem in item.ProjectItems)
                {
                    itemNode.Children.Add(GetProjectItemNode(subItem));
                }
            }

            return itemNode;
        }
        
        private string GetProjectItemType(ProjectItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (item.SubProject != null)
            {
                return "project";
            }
            if (item.ProjectItems.Count > 0)
            {
                return "folder";
            }
            return "file";
        }


        private async Task SendContextUpdateAsync()
        {
            if (_isDisposed || _webView?.CoreWebView2 == null) return;

            var message = new VsMessage
            {
                Action = "UpdateCodeContext",
                Payload = JsonUtils.Serialize(_currentContext)
            };

            var messageToSend = new { type = nameof(VsMessage), payload = message };
            var json = JsonUtils.Serialize(messageToSend);

            await _webView.Dispatcher.InvokeAsync(() =>
            {
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            });
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                _solutionEvents.Opened -= SolutionChangedHandler;
                _solutionEvents.AfterClosing -= SolutionChangedHandler;
                _solutionEvents.ProjectAdded -= SolutionProjectChangedHandler;
                _solutionEvents.ProjectRemoved -= SolutionProjectChangedHandler;
                _solutionEvents.ProjectRenamed -= SolutionProjectRenamedHandler;

                _windowEvents.WindowActivated -= WindowActivatedHandler;
                _documentEvents.DocumentSaved -= DocumentSavedHandler;
            });

            _updateSemaphore?.Dispose();
        }
    }
}
