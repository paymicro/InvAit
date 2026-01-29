let vsBridgeHandler;

// Функция для установки .NET обработчика UIBlazor
window.setVsBridgeHandler = function (dotNetRef) {
    vsBridgeHandler = dotNetRef;
    console.log('Visual Studio bridge handler initialized');
    return "OK";
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

//определение темы
const isDarkMode = window.matchMedia('(prefers-color-scheme: dark)').matches;

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

let lastSavedRange = null;

window.editorFunctions = {
    // Вызывать эту функцию на onmousedown или onfocusout редактора
    saveSelection: function () {
        const sel = window.getSelection();
        if (sel.rangeCount > 0) {
            lastSavedRange = sel.getRangeAt(0);
        }
    },

    insertChip: function (elementId, html, charsToDelete) {
        const el = document.getElementById(elementId);
        const sel = window.getSelection();

        // Восстанавливаем фокус и range
        el.focus();
        if (lastSavedRange) {
            sel.removeAllRanges();
            sel.addRange(lastSavedRange);
        }

        if (sel.rangeCount > 0) {
            let range = sel.getRangeAt(0);

            if (charsToDelete > 0) {
                // Проверка границ, чтобы не уйти в минус (ошибка Offset)
                const start = Math.max(0, range.startOffset - charsToDelete);
                range.setStart(range.startContainer, start);
                range.deleteContents();
            }

            const temp = document.createElement("div");
            temp.innerHTML = html;
            const node = temp.firstChild;

            range.insertNode(node);
            range.setStartAfter(node);
            range.collapse(true);
            sel.removeAllRanges();
            sel.addRange(range);

            // Обнуляем после вставки
            lastSavedRange = null;
        }
    },

    getWordBeforeCursor: function () {
        const selection = window.getSelection();
        if (selection.rangeCount > 0) {
            const range = selection.getRangeAt(0);
            const container = range.startContainer;

            if (container.nodeType === Node.TEXT_NODE) {
                const textBefore = container.textContent.substring(0, range.startOffset);
                const match = textBefore.match(/@(\w*)$/);
                if (match) {
                    return { query: match[1], length: match[0].length };
                }
            }
        }
        return null;
    },

    getCursorCoordinates: function (elementId) {
        const sel = window.getSelection();
        if (sel.rangeCount > 0) {
            const range = sel.getRangeAt(0);
            const rect = range.getBoundingClientRect();
            const editorRect = document.getElementById(elementId).getBoundingClientRect();

            // Возвращаем координаты относительно ВЕРХНЕГО ЛЕВОГО угла РЕДАКТОРА
            return {
                top: Math.round(rect.top - editorRect.top),
                left: Math.round(rect.left - editorRect.left)
            };
        }
        return { top: 0, left: 0 };
    },

    setupPasteInterop: function (elementId) {
        const el = document.getElementById(elementId);
        if (!el) return;

        el.addEventListener('paste', function (e) {
            e.preventDefault(); // Блокируем стандартную вставку (картинки, стили)

            // Извлекаем только чистый текст
            const text = (e.originalEvent || e).clipboardData.getData('text/plain');

            const sel = window.getSelection();
            if (!sel.rangeCount) return;

            const range = sel.getRangeAt(0);
            range.deleteContents();

            // Создаем текстовый узел и вставляем его
            const textNode = document.createTextNode(text);
            range.insertNode(textNode);

            // Перемещаем курсор в конец вставленного текста
            range.setStartAfter(textNode);
            range.collapse(true);
            sel.removeAllRanges();
            sel.addRange(range);
        });

        el.addEventListener('drop', function (e) {
            e.preventDefault(); // Запрещаем бросать файлы и картинки
        });
    },

    getText: function (elementId) {
        const el = document.getElementById(elementId);
        return el.textContent
    }
};