window.postVsMessage = msg => chrome.webview.postMessage(msg);

window.receiveVsResponse = function (response) {
    DotNet.invokeMethodAsync('UIBlazor', 'HandleVsResponse', response);
};