let markdownRenderer = null;

const highlightExt = markedHighlight.markedHighlight({
    langPrefix: 'hljs language-',
    highlight(code, lang, info) {
        const language = hljs.getLanguage(lang) ? lang : 'plaintext';
        return hljs.highlight(code, { language }).value;
    }
});

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
                        hljs.COMMENT('/\\*', '\\*/'),
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

        // Custom renderer for mermaid code blocks
        const renderer = new window.marked.Renderer();
        const originalCode = renderer.code.bind(renderer);

        renderer.code = function (code) {
            if (code?.lang === 'mermaid' && code?.text !== undefined) {
                const id = 'mermaid-' + Math.random().toString(36).substr(2, 9);
                return `<div class="mermaid" id="${id}">${code.text}</div>`;
            }
            return originalCode(code);
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
    return renderer.parse(text);
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
                theme: isDarkMode ? 'dark' : 'base'
            });
            const diagrams = element.querySelectorAll('.mermaid');
            diagrams.forEach((diagram, index) => {
                const id = diagram.id;
                const text = diagram.textContent;
                window.mermaid.render('mermaid-render-' + index, text)
                    .then((result) => {
                        diagram.innerHTML = result.svg;
                    })
                    .catch((error) => {
                        console.error('Mermaid render error:', error);
                        diagram.innerHTML = '<pre>' + text + '</pre>';
                    });
            });
        }
    }
}