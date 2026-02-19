using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using InvAit.Utils;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Wpf;
using Shared.Contracts;

namespace InvAit.Agent
{
    public sealed class VsCodeContextPublisher : IDisposable
    {
        private readonly DTE2 _dte;
        private readonly WebView2 _webView;
        private readonly SolutionEvents _solutionEvents;
        private readonly WindowEvents _windowEvents;
        private readonly DocumentEvents _documentEvents;
        private readonly SelectionEvents _selectionEvents;

        private bool _isDisposed;
        private bool _isUiReady;
        private VsCodeContext _lastContext;
        private DateTime _lastUpdate = DateTime.MinValue;
        private const int _throttleMs = 500;

        private VsCodeContextPublisher(DTE2 dte, WebView2 webView)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _dte = dte;
            _webView = webView;

            _solutionEvents = _dte.Events.SolutionEvents;
            _windowEvents = _dte.Events.WindowEvents;
            _documentEvents = _dte.Events.DocumentEvents;
            _selectionEvents = _dte.Events.SelectionEvents;
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

            _solutionEvents.Opened += OnContextChanged;
            _solutionEvents.AfterClosing += OnContextChanged;
            _solutionEvents.ProjectAdded += OnProjectChanged;
            _solutionEvents.ProjectRemoved += OnProjectChanged;
            _solutionEvents.ProjectRenamed += OnProjectRenamed;

            _windowEvents.WindowActivated += OnWindowActivated;
            _documentEvents.DocumentSaved += OnDocumentSaved;
            _selectionEvents.OnChange += OnSelectionChanged;
        }

        public async Task PushInitialContextAsync()
        {
            _isUiReady = true;
            if (_lastContext != null)
            {
                await SendContextUpdateAsync(_lastContext);
            }
            else
            {
                await UpdateContextAsync();
            }
        }

        private void OnSelectionChanged() => DebouncedUpdate();
        private void OnDocumentSaved(Document document) => DebouncedUpdate();
        private void OnWindowActivated(Window gotFocus, Window lostFocus) => DebouncedUpdate();
        private void OnProjectRenamed(Project project, string oldName) => DebouncedUpdate();
        private void OnProjectChanged(Project project) => DebouncedUpdate();
        private void OnContextChanged() => DebouncedUpdate();

        private void DebouncedUpdate()
        {
            if ((DateTime.UtcNow - _lastUpdate).TotalMilliseconds < _throttleMs) return;
            _lastUpdate = DateTime.UtcNow;
            UpdateContextAsync().FireAndForget();
        }

        private async Task UpdateContextAsync()
        {
            if (_isDisposed) return;

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var context = new VsCodeContext();

                // 1. Get Solution Files
                if (_dte.Solution != null && _dte.Solution.IsOpen)
                {
                    context.SolutionFiles = GetAllSolutionFiles();
                }

                // 2. Get Active Document info
                if (_dte.ActiveDocument != null)
                {
                    context.ActiveFilePath = _dte.ActiveDocument.FullName;

                    var textDoc = (TextDocument)_dte.ActiveDocument.Object("TextDocument");
                    if (textDoc != null)
                    {
                        var startPoint = textDoc.StartPoint.CreateEditPoint();
                        context.ActiveFileContent = startPoint.GetText(textDoc.EndPoint);
                    }

                    if (_dte.ActiveDocument.Selection is TextSelection selection)
                    {
                        context.SelectionStartLine = selection.TopLine;
                        context.SelectionEndLine = selection.BottomLine;
                    }
                }

                _lastContext = context;

                if (_isUiReady)
                {
                    await SendContextUpdateAsync(context);
                }
            }
            catch (Exception ex)
            {
                await Logger.LogAsync($"Error updating context: {ex.Message}", "ERROR");
            }
        }

        private List<string> GetAllSolutionFiles()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var files = new List<string>();

            foreach (Project project in _dte.Solution.Projects)
            {
                files.AddRange(GetProjectFiles(project));
            }

            return [.. files.Distinct()];
        }

        private List<string> GetProjectFiles(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var files = new List<string>();

            if (project.Kind == ProjectKinds.vsProjectKindSolutionFolder)
            {
                if (project.ProjectItems != null)
                {
                    foreach (ProjectItem item in project.ProjectItems)
                    {
                        if (item.SubProject != null)
                        {
                            files.AddRange(GetProjectFiles(item.SubProject));
                        }
                    }
                }
            }
            else
            {
                if (project.ProjectItems != null)
                {
                    foreach (ProjectItem item in project.ProjectItems)
                    {
                        files.AddRange(GetProjectItemFiles(item));
                    }
                }
            }

            return files;
        }

        private List<string> GetProjectItemFiles(ProjectItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var files = new List<string>();

            if (Guid.TryParse(item.Kind, out var kindGuid) && kindGuid == new Guid(Constants.vsProjectItemKindPhysicalFile))
            {
                if (item.FileCount > 0)
                {
                    try
                    {
                        files.Add(item.FileNames[0]);
                    }
                    catch { /* Ignore */ }
                }
            }

            if (item.ProjectItems != null)
            {
                foreach (ProjectItem subItem in item.ProjectItems)
                {
                    files.AddRange(GetProjectItemFiles(subItem));
                }
            }

            if (item.SubProject != null)
            {
                files.AddRange(GetProjectFiles(item.SubProject));
            }

            return files;
        }

        private async Task SendContextUpdateAsync(VsCodeContext context)
        {
            if (_isDisposed || _webView?.CoreWebView2 == null) return;

            var message = new VsMessage
            {
                Action = "UpdateCodeContext",
                Payload = JsonUtils.Serialize(context)
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

                _solutionEvents.Opened -= OnContextChanged;
                _solutionEvents.AfterClosing -= OnContextChanged;
                _solutionEvents.ProjectAdded -= OnProjectChanged;
                _solutionEvents.ProjectRemoved -= OnProjectChanged;
                _solutionEvents.ProjectRenamed -= OnProjectRenamed;

                _windowEvents.WindowActivated -= OnWindowActivated;
                _documentEvents.DocumentSaved -= OnDocumentSaved;
                _selectionEvents.OnChange -= OnSelectionChanged;
            });
        }
    }
}
