let vsBridgeHandler;

// Функция для установки .NET обработчика UIBlazor
window.setVsBridgeHandler = function (dotNetRef) {
    vsBridgeHandler = dotNetRef;
    console.log('Visual Studio bridge handler initialized');
};

// сообщение UIBlazor -> InvGen
// msg - объект
window.postVsMessage = msg => {
    if (window.chrome?.webview) {
        window.chrome.webview.postMessage(msg);
        // TODO удалить логи или включать их опционально
        console.log("VsRequest: ", msg);
        return "OK";
    } else {
        console.warn("WebView2 API не обнаружен. Сообщение не отправлено:", msg);
        return "FAIL";
    }
};

// Проверка, запущено ли приложение в WebView2
if (window.chrome && window.chrome.webview) {
    // сообщение InvGen -> UIBlazor
    // отправляются через webView.CoreWebView2.PostWebMessageAsJson
    window.chrome.webview.addEventListener('message', ({ data }) => {
        if (!vsBridgeHandler) {
            console.error('Visual Studio bridge handler is not initialized');
            return;
        }

        switch (data.type) {
            case 'VsResponse':
                // TODO удалить логи или включать их опционально
                console.log("VsResponse: ", data.payload);

                // вызов метода JSInvokable
                vsBridgeHandler.invokeMethodAsync('HandleVsResponse', data.payload)
                    .catch(err => console.error('Error invoking HandleVsResponse:', err));
                break;
            case 'VsMessage':
                // TODO удалить логи или включать их опционально
                console.log("Message: ", data.payload);

                // вызов метода JSInvokable
                vsBridgeHandler.invokeMethodAsync('HandleVsMessage', data.payload)
                    .catch(err => console.error('Error invoking HandleVsMessage:', err));
                break;
            default:
                console.warn("Неизвестный тип сообщения:", data.type);
        }
    });
} else {
    console.warn("Приложение запущено без WebView2");
}

function scrollToBottomIfNeeded(selector, threshold = 100) {
    setTimeout(() => {
        const container = document.querySelector(selector);
        if (container) {
            // Проверяем, находится ли пользователь близко к низу
            const isNearBottom =
                container.scrollHeight - container.scrollTop - container.clientHeight <= threshold;

            if (isNearBottom) {
                container.scrollTop = container.scrollHeight;
            }
        }
    }, 100);
}