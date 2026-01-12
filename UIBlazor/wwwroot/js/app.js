let vsBridgeHandler;

// Функция для установки .NET обработчика UIBlazor
// Ее вызывает VsBridgeProxy
window.setVsBridgeHandler = function (dotNetRef) {
    vsBridgeHandler = dotNetRef;
    console.log('VS Bridge handler initialized');
};

// сообщение UIBlazor -> InvGen
// msg - объект
window.postVsMessage = msg => chrome.webview.postMessage(msg);

// сообщение InvGen -> UIBlazor
// response - объект
// isMessage - boolean. true - инициатор InvGen. false - ответ на запрос выше.
window.receiveVsResponse = function (response, isMessage) {
    if (vsBridgeHandler) {
        vsBridgeHandler.invokeMethodAsync('HandleVsResponse', JSON.stringify(response), isMessage)
            .catch(err => console.error('Error invoking HandleVsResponse:', err));
    } else {
        console.error('VS Bridge handler not initialized yet');
    }
};