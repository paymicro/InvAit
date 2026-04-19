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
                // console.log("Message: ", data.payload);

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

window.initChatAutoScroll = function (selector, threshold = 70) {
    const container = document.querySelector(selector);
    if (!container) return;

    let isAtBottom = true;

    // 1. Следим за ручным скроллом пользователя
    container.addEventListener('scroll', () => {
        // Проверяем, находится ли пользователь внизу (с небольшим порогом)
        const position = container.scrollHeight - container.scrollTop - container.clientHeight;
        isAtBottom = position <= threshold;
    });

    // 2. Создаем наблюдатель за изменениями в DOM
    const observer = new MutationObserver((mutations) => {
        // Если до изменения мы были внизу - скроллим к новой нижней границе
        if (isAtBottom) {
            // Используем requestAnimationFrame для синхронизации с отрисовкой браузера
            requestAnimationFrame(() => {
                container.scrollTop = container.scrollHeight;
            });
        }
    });

    // Настраиваем наблюдение за добавлением новых элементов (childList)
    observer.observe(container, {
        childList: true,
        subtree: true // важно, если сообщения вложены глубоко
    });
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
        const text = el.value || el.innerText || '';
        const cursorPos = el.selectionStart;

        // Находим границы текущей строки
        let lineStart, lineEnd;

        if (isHome) {
            // Ищем начало текущей строки (после ближайшего \n слева)
            const lastNewLine = text.lastIndexOf('\n', cursorPos - 1);
            lineStart = lastNewLine === -1 ? 0 : lastNewLine + 1;
        } else {
            // Ищем конец текущей строки (до ближайшего \n справа)
            const nextNewLine = text.indexOf('\n', cursorPos);
            lineEnd = nextNewLine === -1 ? text.length : nextNewLine;
        }

        const targetPos = isHome ? lineStart : lineEnd;

        if (isShift) {
            // Логика выделения (Selection)
            if (isHome) {
                el.setSelectionRange(targetPos, cursorPos, 'backward');
            } else {
                el.setSelectionRange(cursorPos, targetPos, 'forward');
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
