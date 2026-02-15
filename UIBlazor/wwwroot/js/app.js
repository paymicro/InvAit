let vsBridgeHandler;

// Функция для установки .NET обработчика UIBlazor
window.setVsBridgeHandler = function (dotNetRef) {
    vsBridgeHandler = dotNetRef;
    console.log('Visual Studio bridge handler initialized');
    return "OK";
};

// сообщение UIBlazor -> InvAit
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
    // сообщение InvAit -> UIBlazor
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

//определение темы
const isDarkMode = window.matchMedia('(prefers-color-scheme: dark)').matches;

window.scrollToBottom = function (selector, force = false, threshold = 60) {
    const container = document.querySelector(selector);
    if (container) {
        requestAnimationFrame(() => { // чтобы дождаться перерисовки DOM (особенно важно для Blazor)
            const isNearBottom =
                container.scrollHeight - container.scrollTop - container.clientHeight <= threshold;

            if (force || isNearBottom) {
                container.scrollTop = container.scrollHeight;
            }
        });
    }
};

// Автоматическое изменение высоты textarea
window.autoResizeTextarea = function (element, auto = false) {
    if (!element) return;

    // Сбрасываем высоту до минимума
    element.style.height = 'auto';

    if (auto) return;
    // Устанавливаем новую высоту на основе scrollHeight
    const newHeight = Math.min(element.scrollHeight, 200); // Максимум ~10 строк
    element.style.height = newHeight + 'px';
};

let chatHandler;
window.setChatHandler = function (dotNetRef) {
    chatHandler = dotNetRef;
    return "OK";
};

window.approveTool = function (messageId, callId, approved) {
    if (chatHandler) {
        chatHandler.invokeMethodAsync('HandleToolApproval', messageId, callId, approved);
    }
};

// для Home и End. Приходится их отправлять программно т.к. они перехватываются в VS.
window.handleNavigationKey = function (key, isShift) {
    const el = document.activeElement;
    const isHome = key === 'Home';

    // Если фокус в текстовом поле или контенте
    if (el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.isContentEditable)) {
        const targetPos = isHome ? 0 : (el.value?.length || el.innerText?.length || 0);

        if (isShift) {
            // Логика выделения (Selection)
            if (isHome) {
                el.setSelectionRange(0, el.selectionEnd, 'backward');
            } else {
                el.setSelectionRange(el.selectionStart, targetPos, 'forward');
            }
        } else {
            // Просто перенос курсора
            el.setSelectionRange(targetPos, targetPos);
        }
        el.focus();
    } else {
        // Если фокус не в тексте — скроллим страницу
        window.scrollTo({
            top: isHome ? 0 : document.body.scrollHeight,
            behavior: 'smooth'
        });
    }
};
