// Monaco bootstrap for the JSON editors. The bundle only pulls the editor core, the JSON
// language service, and the editing contributions the views rely on; importing the package
// root would also register the TypeScript, CSS, and HTML services, whose workers the Web UI
// never needs.
import * as monaco from 'monaco-editor/editor/editor.api.js'
import 'monaco-editor/editor/browser/coreCommands.js'
import 'monaco-editor/editor/contrib/bracketMatching/browser/bracketMatching.js'
import 'monaco-editor/editor/contrib/clipboard/browser/clipboard.js'
import 'monaco-editor/editor/contrib/comment/browser/comment.js'
import 'monaco-editor/editor/contrib/contextmenu/browser/contextmenu.js'
import 'monaco-editor/editor/contrib/find/browser/findController.js'
import 'monaco-editor/editor/contrib/folding/browser/folding.js'
import 'monaco-editor/editor/contrib/format/browser/formatActions.js'
import 'monaco-editor/editor/contrib/hover/browser/hoverContribution.js'
import 'monaco-editor/editor/contrib/linesOperations/browser/linesOperations.js'
import 'monaco-editor/editor/contrib/suggest/browser/suggestController.js'
import 'monaco-editor/editor/contrib/tokenization/browser/tokenization.js'
import 'monaco-editor/editor/contrib/wordOperations/browser/wordOperations.js'
import 'monaco-editor/languages/features/json/register.js'
import EditorWorker from 'monaco-editor/editor/editor.worker.js?worker&inline'
import JsonWorker from 'monaco-editor/language/json/json.worker.js?worker&inline'

// Both workers are inlined as blob URLs so the editor needs no extra request for them.
globalThis.MonacoEnvironment = {
  getWorker: (_workerId: string, label: string) => (label === 'json' ? new JsonWorker() : new EditorWorker()),
}

export default monaco
