let markdownRenderer = null;
const mermaidCache = new Map();

const highlightExt = markedHighlight.markedHighlight({
    langPrefix: 'hljs language-',
    highlight(code, lang, info) {
        const language = hljs.getLanguage(lang) ? lang : 'plaintext';
        return hljs.highlight(code, { language }).value;
    }
});

// Global copy function
window.copyCode = function (btn, elementId) {
    const codeElement = document.getElementById(elementId);
    if (!codeElement) return;

    // Get text content (removes HTML tags inserted by syntax highlighter)
    const text = codeElement.textContent;

    navigator.clipboard.writeText(text).then(() => {
        const icon = btn.querySelector('i');
        const span = btn.querySelector('span');

        if (icon) {
            icon.className = 'fas fa-check';
        }
        if (span) {
            span.textContent = 'Copied!';
        }

        setTimeout(() => {
            if (icon) {
                icon.className = 'fas fa-copy';
            }
            if (span) {
                span.textContent = 'Copy';
            }
        }, 2000);
    }).catch(err => {
        console.error('Failed to copy:', err);
    });
};

function initializeMarkdownRenderer() {
    if (markdownRenderer) return markdownRenderer;

    // Configure highlight.js for C# and SQL
    if (window.hljs) {
        // Register C# language if not already registered
        if (!window.hljs.getLanguage('csharp')) {
            // Basic C# patterns - in production use full language definition
            window.hljs.registerLanguage('csharp', function (hljs) {
                return {
                    keywords: 'abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while',
                    contains: [
                        hljs.COMMENT('//', '$'),
                        hljs.COMMENT('/\*', '\*/'),
                        hljs.C_LINE_COMMENT_MODE,
                        hljs.C_BLOCK_COMMENT_MODE,
                        {
                            className: 'string',
                            begin: '@"', end: '"',
                            contains: [{ begin: '""' }]
                        },
                        hljs.APOS_STRING_MODE,
                        hljs.QUOTE_STRING_MODE,
                        hljs.NUMBER_MODE
                    ]
                };
            });
        }

        // Register SQL language if not already registered
        if (!window.hljs.getLanguage('sql')) {
            window.hljs.registerLanguage('sql', function (hljs) {
                return {
                    case_insensitive: true,
                    keywords: 'select from where insert update delete create drop table index database schema view procedure function trigger alter add column primary foreign key constraint unique check default null not and or between like in exists join inner left right outer full on group by having order asc desc limit offset union all distinct as case when then else end',
                    contains: [
                        hljs.C_LINE_COMMENT_MODE,
                        hljs.C_BLOCK_COMMENT_MODE,
                        {
                            className: 'string',
                            begin: '\'', end: '\'',
                            contains: [{ begin: '\'\'' }]
                        },
                        {
                            className: 'string',
                            begin: '"', end: '"'
                        },
                        hljs.NUMBER_MODE
                    ]
                };
            });
        }
    }

    // Configure marked with highlighting
    if (window.marked && window.markedHighlight) {
        markdownRenderer = window.marked;

        // Use marked-highlight extension
        markdownRenderer.use(highlightExt);

        // Custom renderer for mermaid code blocks and copy button
        const renderer = new window.marked.Renderer();
        // const originalCode = renderer.code.bind(renderer); // Not needed if we fully override

        renderer.code = function (code, infostring, escaped) {
            let textContent = code;
            let language = infostring;

            // Check if code is an object (Token) - handling legacy/mermaid logic
            if (typeof code === 'object') {
                if (code.lang === 'mermaid' && code.text !== undefined) {
                    const id = 'mermaid-' + Math.random().toString(36).substr(2, 9);
                    return `<div class="mermaid" id="${id}">${code.text}</div>`;
                }
                textContent = code.text;
                language = code.lang;
            }

            const lang = (language || '').match(/\S*/)[0];

            if (lang === 'mermaid') {
                const id = 'mermaid-' + Math.random().toString(36).substr(2, 9);
                return `<div class="mermaid" id="${id}">${textContent}</div>`;
            }

            const id = 'code-' + Math.random().toString(36).substr(2, 9);
            const label = lang ? lang.toUpperCase() : '';

            // Note: textContent here is already highlighted HTML by marked-highlight

            return `\
            <div class="code-block-wrapper">
                <div class="code-header">
                    <span class="code-lang">${label}</span>
                    <button class="code-copy-btn" onclick="window.copyCode(this, '${id}')" title="Copy code">
                        <i class="fas fa-copy"></i>
                        <span>Copy</span>
                    </button>
                </div>
                <pre><code id="${id}" class="hljs language-${lang}">${textContent}</code></pre>
            </div>\
            `;
        };

        markdownRenderer.use({ renderer });
    } else {
        // Fallback renderer
        markdownRenderer = {
            parse: function (text) {
                return text.replace(/\n/g, '<br>');
            }
        };
    }

    return markdownRenderer;
}

function renderMarkdown(text) {
    const renderer = initializeMarkdownRenderer();

    // Detect incomplete mermaid blocks at the end of text (common during streaming)
    // If we find a ```mermaid that is not followed by a closing ``` 
    // we change it to ```text to avoid partial/broken rendering.
    let processedText = text;
    const lastMermaidIndex = text.lastIndexOf('```mermaid');
    if (lastMermaidIndex !== -1) {
        const afterMermaid = text.substring(lastMermaidIndex + 10);
        if (afterMermaid.indexOf('```') === -1) {
            processedText = text.substring(0, lastMermaidIndex) + '```text' + afterMermaid;
        }
    }

    return renderer.parse(processedText);
}

function renderMarkdownToElement(elementId, text) {
    const element = document.getElementById(elementId);
    if (element) {
        element.innerHTML = renderMarkdown(text);

        // Initialize mermaid diagrams
        if (window.mermaid) {
            window.mermaid.initialize({
                startOnLoad: false,
                suppressErrorRendering: true,
                // isDarkMode берется из app.js
                theme: isDarkMode ? 'dark' : 'base'
            });

            const diagrams = element.querySelectorAll('.mermaid');
            diagrams.forEach((diagram, index) => {
                const content = diagram.textContent;
                // Use a globally unique ID for rendering to avoid conflicts
                const renderId = 'mermaid-svg-' + elementId.replace(/[^a-zA-Z0-9]/g, '') + '-' + index;

                // Check cache
                const cached = mermaidCache.get(renderId);
                if (cached && cached.code === content) {
                    if (cached.error) {
                        renderMermaidError(diagram, content, cached.errorMessage);
                    } else {
                        diagram.innerHTML = cached.svg;
                    }
                    return;
                }

                window.mermaid.render(renderId, content)
                    .then((result) => {
                        diagram.innerHTML = result.svg;
                        mermaidCache.set(renderId, { code: content, svg: result.svg });

                        // Simple cleanup to prevent memory leaks
                        if (mermaidCache.size > 200) {
                            const firstKey = mermaidCache.keys().next().value;
                            mermaidCache.delete(firstKey);
                        }
                    })
                    .catch((error) => {
                        console.error('Mermaid render error:', error);
                        const errorMessage = error.message || error.toString();

                        // Save failure to cache so we don't try again for the same content/id
                        mermaidCache.set(renderId, {
                            code: content,
                            error: true,
                            errorMessage: errorMessage
                        });

                        renderMermaidError(diagram, content, errorMessage);
                    });
            });
        }
    }
}

function renderMermaidError(element, code, error) {
    // Escaping code content to prevent HTML injection in pre tag
    const escapedCode = code
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#039;");

    element.innerHTML = `
        <div class="mermaid-error-container">
            <div class="mermaid-error-header">
                <i class="fas fa-exclamation-triangle"></i> Mermaid Render Error
            </div>
            <div class="mermaid-error-body">
                <div class="mermaid-error-message">${error}</div>
                <details class="mermaid-error-details">
                    <summary>Show diagram code</summary>
                    <pre><code>${escapedCode}</code></pre>
                </details>
            </div>
        </div>
    `;
}
