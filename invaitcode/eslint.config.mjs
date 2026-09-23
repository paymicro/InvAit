import typescriptEslint from "typescript-eslint";
import stylistic from '@stylistic/eslint-plugin';

export default [{
    files: ["**/*.ts"],
}, {
    plugins: {
        "@typescript-eslint": typescriptEslint.plugin,
        '@stylistic': stylistic,
    },

    languageOptions: {
        parser: typescriptEslint.parser,
        ecmaVersion: 2022,
        sourceType: "module",
    },

    rules: {
      '@stylistic/indent': ['error', 4],
      '@stylistic/semi': ['error', 'always'],
      '@stylistic/no-trailing-spaces' : ['error'],
      '@stylistic/eol-last' : 'error',
      '@stylistic/max-len' : [2, 300, {
          ignoreUrls : true,
          ignoreTrailingComments : true,
          ignoreRegExpLiterals : true,
        },
      ],
    },
}];