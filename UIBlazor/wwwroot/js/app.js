// Функция для установки .NET обработчика UIBlazor
window.setVsBridgeHandler = function (dotNetRef) {
    window.vsBridgeHandler = dotNetRef;
    console.log('Visual Studio bridge handler initialized');
    return "OK";
};

const isVSCode = window.parent !== window;

// Глобальный флаг для C# (Blazor JS interop может не сохранять `this` при вызове методов объекта,
// поэтому используем простую глобальную функцию вместо метода на объекте)
window.isVsCodeEnv = function () {
    console.log('[InvAit] isVsCodeEnv check, isVSCode =', isVSCode);
    return isVSCode;
};

// VSCode localStorage proxy — must run BEFORE Blazor initializes
if (isVSCode) {
    // Initial storage injected by the static server into index.html
    var initialStorage = window.__vscodeInitialStorage__
        ? JSON.parse(window.__vscodeInitialStorage__)
        : {};

    // In-memory cache backed by globalState via postMessage
    var storageCache = initialStorage;

    // Persist a key to VSCode globalState asynchronously
    function persistSet(key, value) {
        window.parent.postMessage({
            target: 'vscode-webview',
            packet: {
                command: 'StorageSet',
                payload: { key: key, value: String(value) }
            }
        }, '*');
    }
    function persistRemove(key) {
        window.parent.postMessage({
            target: 'vscode-webview',
            packet: {
                command: 'StorageRemove',
                payload: { key: key }
            }
        }, '*');
    }

    // Use a real JS Proxy so Object.keys(localStorage) returns storage keys,
    // not method names. This is needed because LocalStorageService.cs calls
    // eval("Object.keys(localStorage)") to enumerate keys.
    var storageProxy = new Proxy({}, {
        get: function(_target, prop) {
            if (prop === 'getItem')
                return function(key) { return storageCache[key] !== undefined ? storageCache[key] : null; };
            if (prop === 'setItem')
                return function(key, value) { storageCache[key] = String(value); persistSet(key, value); };
            if (prop === 'removeItem')
                return function(key) { delete storageCache[key]; persistRemove(key); };
            if (prop === 'key')
                return function(index) { var ks = Object.keys(storageCache); return ks[index] || null; };
            if (prop === 'clear')
                return function() { var ks = Object.keys(storageCache); for (var i = 0; i < ks.length; i++) { delete storageCache[ks[i]]; persistRemove(ks[i]); } };
            if (prop === 'length')
                return Object.keys(storageCache).length;
            // Direct property access (e.g. localStorage['mykey'])
            if (typeof prop === 'string' && prop in storageCache)
                return storageCache[prop];
            return undefined;
        },
        ownKeys: function() {
            return Object.keys(storageCache);
        },
        getOwnPropertyDescriptor: function(_target, prop) {
            if (typeof prop === 'string' && prop in storageCache) {
                return { enumerable: true, configurable: true, writable: true, value: storageCache[prop] };
            }
            return undefined;
        }
    });

    // Replace localStorage with our proxy
    Object.defineProperty(window, 'localStorage', {
        value: storageProxy,
        writable: false,
        configurable: true
    });
}

// ОТПРАВКА: UIBlazor -> Бэкенд
window.postVsMessage = msg => {
    if (window.chrome?.webview) {
        // Код для Visual Studio 2026
        window.chrome.webview.postMessage(msg);
        console.log("Visual Studio Request: ", msg);
        return "OK";
    } else if (isVSCode) {
        // Код для VS Code (отправляем родителю iframe)
        window.parent.postMessage({ target: 'vscode-webview', packet: msg }, '*');
        console.log("VSCode Request: ", msg);
        return "OK";
    } else {
        console.warn("API связи не обнаружено. Сообщение не отправлено:", msg);
        return "FAIL";
    }
};

// ПРИЕМ: Бэкенд -> UIBlazor
// Универсальный обработчик входящих сообщений
function handleIncomingMessage(data) {
    if (!window.vsBridgeHandler) {
        console.error('Visual Studio bridge handler is not initialized');
        return;
    }

    switch (data.type) {
        case 'VsResponse':
            console.log("VsResponse: ", data.payload);
            window.vsBridgeHandler.invokeMethodAsync('HandleVsResponse', data.payload)
                .catch(err => console.error('Error invoking HandleVsResponse:', err));
            break;
        case 'VsMessage':
            window.vsBridgeHandler.invokeMethodAsync('HandleVsMessage', data.payload)
                .catch(err => console.error('Error invoking HandleVsMessage:', err));
            break;
        default:
            console.warn("Неизвестный тип сообщения:", data.type);
    }
}

// Подписываемся на события в зависимости от среды
if (window.chrome && window.chrome.webview) {
    // Для Visual Studio
    window.chrome.webview.addEventListener('message', ({ data }) => handleIncomingMessage(data));
} else if (isVSCode) {
    // Для VS Code (слушаем сообщения, пришедшие в iframe)
    window.addEventListener('message', (event) => {
        // Проверяем, что сообщение пришло от бэкенда VS Code
        if (event.data && event.data.source === 'vscode-parent') {
            const msgType = event.data.message?.type;
            // VsStreamingPacket и StorageKeys обрабатываются отдельными слушателями
            if (msgType === 'VsStreamingPacket' || msgType === 'StorageKeys') return;
            handleIncomingMessage(event.data.message);
        }
    });
} else {
    console.warn("Приложение запущено вне сред Visual Studio");
}

//определение темы
const isDarkMode = window.matchMedia('(prefers-color-scheme: dark)').matches;

// скролл к самому низу сообщений
window.scrollToAnchor = function() {
    const anchor = document.querySelector(".anchor");
    if (anchor) {
        // scrollIntoView плавно или мгновенно доведет скролл до низа
        anchor.scrollIntoView({ behavior: 'instant', block: 'end' });
    }
};

// скролл к инструменту, требующему действия пользователя (апрув или ask_user)
// подсветка цветом + уведомление
window.scrollToToolCall = function(toolCallId) {
    const el = document.querySelector(`[data-toolcall-id="${toolCallId}"]`);
    if (!el) return;

    // Сначала разворачиваем родительский sub-agent блок, если он свёрнут
    const subAgentBlock = el.closest('.subagent-block');
    if (subAgentBlock) {
        const body = subAgentBlock.querySelector('.subagent-body');
        if (!body) {
            // Sub-agent свёрнут — разворачиваем через клик по заголовку
            const header = subAgentBlock.querySelector('.subagent-header');
            if (header) header.click();
        }
    }

    // Небольшая задержка, чтобы DOM успел обновиться после разворачивания
    setTimeout(() => {
        el.scrollIntoView({ behavior: 'smooth', block: 'center' });

        // Подсветка
        el.classList.add('tool-call-highlight');
        setTimeout(() => el.classList.remove('tool-call-highlight'), 4000);
    }, subAgentBlock ? 150 : 0);
};

// скролл диалога сабагента к последнему сообщению
window.scrollSubAgentToBottom = function(subAgentId) {
    const block = document.querySelector(`[data-subagent-id="${subAgentId}"]`);
    if (!block) return;

    // .subagent-body is the scroll container (has max-height + overflow-y: auto)
    // .subagent-conversation is not a scroll container itself
    const body = block.querySelector('.subagent-body');
    if (body) {
        body.scrollTop = body.scrollHeight;
    }
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

// Module-level variable — НЕ используем `this` т.к. Blazor JS interop
// может вызывать функцию без сохранения this контекста
const _pendingNetworkRequests = new Map();

window.vscodeInterop = {
    sendNetworkRequest: function (requestId, url, method, headers, body, dotNetRef) {
        console.log('[InvAit] sendNetworkRequest:', method, url);
        _pendingNetworkRequests.set(requestId, dotNetRef);

        window.parent.postMessage({
            target: 'vscode-webview',
            packet: {
                command: 'NetworkProxyRequest',
                payload: { requestId, url, method, headers, body }
            }
        }, '*');
    },

    // Обработка разных типов пакетов от VS Code
    handleVsMessage: function (msg) {
        const dotNetRef = _pendingNetworkRequests.get(msg.requestId);
        if (!dotNetRef) return;

        if (msg.type === 'headers') {
            dotNetRef.invokeMethodAsync('ReceiveHeaders', msg.statusCode, msg.statusText || '');
        }
        else if (msg.type === 'chunk') {
            dotNetRef.invokeMethodAsync('ReceiveChunk', msg.chunk);
        }
        else if (msg.type === 'end') {
            dotNetRef.invokeMethodAsync('ReceiveEnd', msg.success, msg.error || null);
            _pendingNetworkRequests.delete(msg.requestId);
            dotNetRef.dispose();
        }
    }
};

// Слушаем сообщения из TS расширения VS Code
window.addEventListener('message', (event) => {
    if (event.data && event.data.source === 'vscode-parent' && event.data.message?.type === 'VsStreamingPacket') {
        window.vscodeInterop.handleVsMessage(event.data.message.payload);
    }
});
