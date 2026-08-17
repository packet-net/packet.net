// ============================================================
// ESLint (flat config, eslint 8.57). `npm run lint` has existed in package.json since
// the panel was created but there was NO config file, so the script could only ever
// fail - and CI never ran it, which is how four `eslint-disable-next-line
// react-hooks/exhaustive-deps` comments came to sit in the tree against a rule nothing
// enforced (#691 C009). ci.yml's web-ui job now runs it alongside build + test.
//
// The rule that earns this file its keep is `no-restricted-imports` at the bottom: the
// screens must not reach into lib/mock.ts. That file is the FAKE NODE (GB7RDG, ports
// vhf-1/uhf-2/link-dn) behind VITE_API_MODE=mock, and every screen that imported a
// fixture for a default or a loading-state fallback was showing real operators an
// invented node (#691 C021/C022). The operator-facing copy, presets and unit helpers
// they legitimately need live in lib/catalogue.ts.
// ============================================================
import js from "@eslint/js";
import tsPlugin from "@typescript-eslint/eslint-plugin";
import tsParser from "@typescript-eslint/parser";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";

export default [
  { ignores: ["dist/**", "node_modules/**", ".shots/**", "public/**"] },

  // The plain-ESM config files at the project root (postcss/tailwind/this one).
  {
    files: ["*.js"],
    languageOptions: { ecmaVersion: 2022, sourceType: "module" },
    rules: { ...js.configs.recommended.rules },
  },

  // The app itself.
  {
    files: ["**/*.ts", "**/*.tsx"],
    // Every `eslint-disable` in the tree has to still be doing something: an unused one
    // fails the run (with --max-warnings 0) rather than sitting there implying a rule.
    linterOptions: { reportUnusedDisableDirectives: true },
    languageOptions: {
      parser: tsParser,
      ecmaVersion: 2022,
      sourceType: "module",
      parserOptions: { ecmaFeatures: { jsx: true } },
    },
    plugins: {
      "@typescript-eslint": tsPlugin,
      "react-hooks": reactHooks,
      "react-refresh": reactRefresh,
    },
    rules: {
      ...js.configs.recommended.rules,
      ...tsPlugin.configs.recommended.rules,
      ...reactHooks.configs.recommended.rules,
      // tsc resolves globals properly (lib.dom + @types/node, `npm run build` runs it);
      // the base rule has no type information and only produces noise on TS.
      "no-undef": "off",
      // Unused code is tsconfig's job here (noUnusedLocals / noUnusedParameters), and it
      // reads the type graph. Keep the lint rule off rather than have two half-answers.
      "@typescript-eslint/no-unused-vars": "off",
      // `catch {}` with a comment saying why is a deliberate idiom in this codebase.
      "no-empty": ["error", { allowEmptyCatch: true }],
    },
  },

  // Screens + components must not import the fixture data.
  {
    files: ["src/screens/**/*.{ts,tsx}", "src/components/**/*.{ts,tsx}"],
    rules: {
      // The pattern catches both spellings: "@/lib/mock" and a relative "../lib/mock".
      "no-restricted-imports": ["error", {
        patterns: [{
          group: ["**/lib/mock"],
          message: "screens must not import fixture data; use the catalogue or the api",
        }],
      }],
    },
  },
];
