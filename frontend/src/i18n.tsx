import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from "react";
import type { NodeSpec } from "./api";
import { nodeTextRu, paramTextRu, categoryRu, templateRu, portTextRu } from "./i18n-nodes.ru";

export type Lang = "en" | "ru";
export const languages: { code: Lang; label: string }[] = [
  { code: "en", label: "English" },
  { code: "ru", label: "Русский" },
];

const en = {
  "nav.pipelines": "Pipelines",
  "nav.runs": "Runs",
  "nav.library": "Library",
  "nav.settings": "Models & tools",
  "nav.tagline": "Node pipelines for game assets.",
  "common.loading": "Loading…",
  "common.cancel": "Cancel",
  "common.save": "Save",
  "common.saved": "Saved",
  "common.delete": "Delete",
  "common.remove": "Remove",
  "common.copy": "Copy",
  "common.copied": "Copied",
  "common.close": "Close",
  "common.enabled": "Enabled",
  "common.disabled": "disabled",
  "common.name": "Name",
  "common.pathCopied": "Path copied",
  "common.language": "Language",
  "status.queued": "queued",
  "status.pending": "pending",
  "status.running": "running",
  "status.succeeded": "succeeded",
  "status.failed": "failed",
  "status.skipped": "skipped",
  "status.canceled": "canceled",
  "status.cached": "cached",
  "time.justNow": "just now",
  "time.minAgo": "{n} min ago",
  "time.hAgo": "{n} h ago",

  "pipelines.title": "Pipelines",
  "pipelines.sub": "Chain your models with nodes: prompt → image → video → 3D → Blender / Unity.",
  "pipelines.new": "+ New pipeline",
  "pipelines.newName": "New pipeline",
  "pipelines.yours": "Your pipelines",
  "pipelines.empty": "No pipelines yet. Start from a template below or create an empty one.",
  "pipelines.colInputs": "Inputs",
  "pipelines.colNodes": "Nodes",
  "pipelines.colUpdated": "Updated",
  "pipelines.duplicate": "Duplicate",
  "pipelines.confirmDelete": "Delete pipeline \"{name}\"? Runs and files are kept.",
  "pipelines.templates": "Templates",
  "pipelines.nodes": "{n} nodes",

  "editor.useCache": "Use cache",
  "editor.useCacheHint": "When settings and inputs are unchanged, the result of an earlier run is reused instead of generating again. Turn off to get a new result on every run.",
  "editor.clearNode": "⟲ Clear node",
  "editor.clearNodeHint": "Forget the saved result of this node and of every node that depends on it — the next run executes them again",
  "editor.clearAll": "⟲ Clear pipeline cache",
  "editor.confirmClearAll": "Forget all saved node results of this pipeline? The next run executes every node again.",
  "editor.cleared": "Cleared nodes: {n}",
  "editor.helpCache": "Unchanged nodes are taken from the cache on every run, so expensive generations are not repeated.",
  "node.noCache": "no cache",
  "editor.undo": "Undo (Ctrl+Z)",
  "editor.redo": "Redo (Ctrl+Shift+Z / Ctrl+Y)",
  "editor.unsaved": "unsaved",
  "editor.details": "details",
  "editor.stop": "Stop",
  "editor.run": "▶ Run",
  "editor.runStarted": "Run started",
  "editor.rerunning": "Rerunning from {id}",
  "editor.confirmRerunDirty": "The pipeline has unsaved edits. Rerun uses the graph of the previous run. Continue?",
  "editor.search": "Search nodes…",
  "editor.paletteHelp": "Drag onto the canvas or double-click. Connect ports of matching colors.",
  "editor.pipeline": "Pipeline",
  "editor.helpTemplates": "Select a node to edit it. Templates in prompts:",
  "editor.helpFilters": "filters",
  "editor.helpFanout": "A list output connected to a single input runs that node once per item (fan-out).",
  "editor.lastError": "Last error",
  "editor.results": "Results",
  "editor.tabSettings": "Settings",
  "editor.tabOutput": "Output",
  "editor.tabLogs": "Logs",
  "editor.title": "Title",
  "editor.execution": "Execution",
  "editor.retries": "Retries",
  "editor.parallel": "Parallel items",
  "editor.bypass": "Bypass (pass first input through)",
  "editor.rerunFrom": "↻ Rerun from this node",
  "editor.rerunFromHint": "Re-execute this node and everything after it; earlier nodes are reused",
  "editor.noOutput": "No output yet — run the pipeline.",
  "editor.noLogs": "No logs yet.",
  "editor.runDialog": "Run pipeline",
  "editor.assetHelp": "Asset id from the Library, a URL, or a local file path — or upload.",
  "editor.upload": "Upload",

  "runs.title": "Runs",
  "runs.sub": "Every execution of a pipeline or single node (UI, REST API or MCP).",
  "runs.empty": "No runs yet.",
  "runs.colStatus": "Status",
  "runs.colPipeline": "Pipeline",
  "runs.colStarted": "Started",
  "runs.colDuration": "Duration",
  "runs.colError": "Error",
  "runs.rerunTag": "rerun",
  "runs.openPipeline": "Open pipeline",

  "run.rerunOf": "rerun of",
  "run.again": "↻ Run again",
  "run.openEditor": "Open in editor",
  "run.started": "Started",
  "run.inputs": "Inputs",
  "run.nodes": "Nodes",
  "run.colNode": "Node",
  "run.colTime": "Time",
  "run.colMessage": "Message",
  "run.items": "{done}/{total} items",
  "run.fromHere": "↻ from here",
  "run.outputs": "Outputs",

  "assets.title": "Library",
  "assets.sub": "Every file uploaded or produced by a node. Use an asset id as a pipeline input.",
  "assets.allKinds": "All kinds",
  "assets.search": "Search name…",
  "assets.drop": "Drop images, videos or models here, or",
  "assets.browse": "browse",
  "assets.orImport": "or import",
  "assets.import": "Import",
  "assets.empty": "Nothing here yet.",
  "assets.uploaded": "Uploaded {n} file(s)",
  "assets.confirmDelete": "Delete {name} permanently?",
  "assets.copyId": "Copy id",
  "assets.idCopied": "Asset id copied",
  "assets.copyPath": "Copy local path",
  "assets.file": "{kind} file",
  "assets.path": "path",
  "assets.copyPathHint": "Copy local path",
  "kind.image": "images", "kind.video": "videos", "kind.model": "3D models", "kind.audio": "audio", "kind.json": "JSON", "kind.file": "files",

  "picker.forOutput": "Connect output {port} to…",
  "picker.forInput": "Feed input {port} from…",
  "picker.search": "Search nodes…",
  "picker.empty": "No node can connect here",
  "node.textPlaceholder": "Type the value…",
  "node.assetPlaceholder": "Asset id, URL or path",
  "node.dropHint": "Drop a file here or upload it",
  "preview.empty": "Run the pipeline — the result will appear here.",
  "preview.expand": "Open full size",
  "preview.download": "Download",
  "preview.partial": "Showing {shown} of {total} — the rest are on the {port}[] output.",
  "preview.showAll": "Show all",

  "param.auto": "(auto: first available)",
  "param.noKey": "no key",
  "param.missing": "missing",
  "param.noMcp": "No MCP servers yet — add them in Models & tools.",

  "settings.title": "Models & tools",
  "settings.sub": "Connect neural networks (cloud APIs, local servers, ComfyUI), MCP servers for agents, and your own nodes.",
  "settings.providers": "Model providers",
  "settings.addProvider": "+ Add provider",
  "settings.colType": "Type",
  "settings.colEndpoint": "Endpoint",
  "settings.colModel": "Default model",
  "settings.colKey": "Key",
  "settings.keyReady": "ready",
  "settings.keyNotNeeded": "not needed",
  "settings.keyMissing": "missing",
  "settings.default": "default",
  "settings.confirmRemoveProvider": "Remove provider {name}? Nodes that use it by name will fail.",
  "settings.providersHelp": "Keys can be literal or env:VAR_NAME (read from the server environment, never stored). Local LLMs: any OpenAI-compatible server — Ollama, LM Studio, vLLM.",
  "settings.editTitle": "Edit {name}",
  "settings.addProviderTitle": "Add provider",
  "settings.nameHelp": "Nodes reference providers by this name.",
  "settings.baseUrl": "Base URL",
  "settings.defaultIs": "Default: {url}",
  "settings.apiKey": "API key",
  "settings.apiKeyHelp": "Literal key or env:VAR. Suggested: {hint}",
  "settings.extra": "Extra (JSON)",
  "settings.extraAnthropic": "{\"fallbacks\": false} disables server-side refusal fallbacks.",
  "settings.mcp": "MCP servers (agent tools)",
  "settings.addMcp": "+ Add MCP server",
  "settings.colTransport": "Transport",
  "settings.colCommand": "Command / URL",
  "settings.test": "Test",
  "settings.mcpHelp": "Blender: install the blender-mcp add-on in Blender, start its server from the sidebar, and make sure uvx (from uv) is on PATH. Unity: any Unity MCP bridge exposing an HTTP endpoint or a stdio command.",
  "settings.addMcpTitle": "Add MCP server",
  "settings.transport": "Transport",
  "settings.stdio": "stdio (local command)",
  "settings.http": "Streamable HTTP",
  "settings.command": "Command",
  "settings.commandHelp": "e.g. uvx, npx, python, node, or a full path",
  "settings.args": "Arguments (one per line)",
  "settings.cwd": "Working folder",
  "settings.env": "Environment (JSON)",
  "settings.envHelp": "Values may be env:VAR, e.g. {\"BLENDER_HOST\":\"localhost\"}",
  "settings.testTitle": "Test: {name}",
  "settings.connecting": "Connecting…",
  "settings.connected": "connected",
  "settings.tools": "{n} tools",
  "settings.custom": "Custom nodes (your neural networks)",
  "settings.newNode": "+ New node",
  "settings.customEmpty": "Wrap any HTTP endpoint as a reusable node with its own inputs and settings. Supports async jobs (poll until done) and downloads result files.",
  "settings.customTitle": "Custom node definition",
  "settings.customHelp": "Inputs become ports; params become settings ({{params.x}}). The http section accepts every setting of the HTTP Request node.",
  "settings.saveNode": "Save node",
  "settings.nodeSaved": "Node saved — it is in the editor palette now",
  "settings.integration": "Use from agents & scripts",
  "settings.snipMcp": "Add ForgeFlow to Claude Code as an MCP server",
  "settings.snipRun": "Run a pipeline over REST and wait for the result",
  "settings.snipNode": "Run one node as a tool",
  "settings.browserKey": "API key for this browser",
  "settings.browserKeyHelp": "Only needed when the server has ForgeFlow:ApiKey set in appsettings.json.",
  "settings.openapi": "OpenAPI spec:",
};

export type MsgKey = keyof typeof en;

const ru: Record<MsgKey, string> = {
  "nav.pipelines": "Конвейеры",
  "nav.runs": "Запуски",
  "nav.library": "Библиотека",
  "nav.settings": "Модели и инструменты",
  "nav.tagline": "Нодовые конвейеры для игровых ассетов.",
  "common.loading": "Загрузка…",
  "common.cancel": "Отмена",
  "common.save": "Сохранить",
  "common.saved": "Сохранено",
  "common.delete": "Удалить",
  "common.remove": "Убрать",
  "common.copy": "Копировать",
  "common.copied": "Скопировано",
  "common.close": "Закрыть",
  "common.enabled": "Включён",
  "common.disabled": "выключен",
  "common.name": "Название",
  "common.pathCopied": "Путь скопирован",
  "common.language": "Язык",
  "status.queued": "в очереди",
  "status.pending": "ожидает",
  "status.running": "выполняется",
  "status.succeeded": "готово",
  "status.failed": "ошибка",
  "status.skipped": "пропущен",
  "status.canceled": "отменён",
  "status.cached": "из кэша",
  "time.justNow": "только что",
  "time.minAgo": "{n} мин назад",
  "time.hAgo": "{n} ч назад",

  "pipelines.title": "Конвейеры",
  "pipelines.sub": "Соединяйте модели нодами: промпт → изображение → видео → 3D → Blender / Unity.",
  "pipelines.new": "+ Новый конвейер",
  "pipelines.newName": "Новый конвейер",
  "pipelines.yours": "Ваши конвейеры",
  "pipelines.empty": "Конвейеров пока нет. Начните с шаблона ниже или создайте пустой.",
  "pipelines.colInputs": "Входы",
  "pipelines.colNodes": "Ноды",
  "pipelines.colUpdated": "Изменён",
  "pipelines.duplicate": "Копия",
  "pipelines.confirmDelete": "Удалить конвейер «{name}»? Запуски и файлы сохранятся.",
  "pipelines.templates": "Шаблоны",
  "pipelines.nodes": "нод: {n}",

  "editor.useCache": "Использовать кэш",
  "editor.useCacheHint": "Если настройки и входы не изменились, берётся результат прошлого запуска вместо новой генерации. Выключите, чтобы получать новый результат при каждом запуске.",
  "editor.clearNode": "⟲ Очистить ноду",
  "editor.clearNodeHint": "Забыть сохранённый результат этой ноды и всех нод, которые от неё зависят — при следующем запуске они выполнятся заново",
  "editor.clearAll": "⟲ Очистить кэш конвейера",
  "editor.confirmClearAll": "Забыть все сохранённые результаты нод этого конвейера? При следующем запуске все ноды выполнятся заново.",
  "editor.cleared": "Очищено нод: {n}",
  "editor.helpCache": "Неизменённые ноды при каждом запуске берутся из кэша, поэтому дорогие генерации не повторяются.",
  "node.noCache": "без кэша",
  "editor.undo": "Отменить (Ctrl+Z)",
  "editor.redo": "Повторить (Ctrl+Shift+Z / Ctrl+Y)",
  "editor.unsaved": "не сохранено",
  "editor.details": "подробнее",
  "editor.stop": "Стоп",
  "editor.run": "▶ Запустить",
  "editor.runStarted": "Запуск начат",
  "editor.rerunning": "Перезапуск с ноды {id}",
  "editor.confirmRerunDirty": "В конвейере есть несохранённые изменения. Перезапуск использует граф прошлого запуска. Продолжить?",
  "editor.search": "Поиск нод…",
  "editor.paletteHelp": "Перетащите на холст или дважды щёлкните. Соединяйте порты одного цвета.",
  "editor.pipeline": "Конвейер",
  "editor.helpTemplates": "Выберите ноду, чтобы настроить её. Подстановки в промптах:",
  "editor.helpFilters": "фильтры",
  "editor.helpFanout": "Если выход-список подключён к одиночному входу, нода выполнится для каждого элемента (fan-out).",
  "editor.lastError": "Последняя ошибка",
  "editor.results": "Результаты",
  "editor.tabSettings": "Настройки",
  "editor.tabOutput": "Результат",
  "editor.tabLogs": "Логи",
  "editor.title": "Заголовок",
  "editor.execution": "Выполнение",
  "editor.retries": "Повторы",
  "editor.parallel": "Параллельно элементов",
  "editor.bypass": "Обход (передать первый вход дальше)",
  "editor.rerunFrom": "↻ Перезапустить с этой ноды",
  "editor.rerunFromHint": "Выполнить заново эту ноду и всё после неё; предыдущие ноды берутся из кэша",
  "editor.noOutput": "Результата пока нет — запустите конвейер.",
  "editor.noLogs": "Логов пока нет.",
  "editor.runDialog": "Запуск конвейера",
  "editor.assetHelp": "ID ассета из Библиотеки, URL или путь к локальному файлу — или загрузите файл.",
  "editor.upload": "Загрузить",

  "runs.title": "Запуски",
  "runs.sub": "Все выполнения конвейеров и отдельных нод (из интерфейса, REST API или MCP).",
  "runs.empty": "Запусков пока нет.",
  "runs.colStatus": "Статус",
  "runs.colPipeline": "Конвейер",
  "runs.colStarted": "Начат",
  "runs.colDuration": "Длительность",
  "runs.colError": "Ошибка",
  "runs.rerunTag": "перезапуск",
  "runs.openPipeline": "Открыть конвейер",

  "run.rerunOf": "перезапуск",
  "run.again": "↻ Запустить снова",
  "run.openEditor": "Открыть в редакторе",
  "run.started": "Запущено",
  "run.inputs": "Входные данные",
  "run.nodes": "Ноды",
  "run.colNode": "Нода",
  "run.colTime": "Время",
  "run.colMessage": "Сообщение",
  "run.items": "элементов: {done}/{total}",
  "run.fromHere": "↻ отсюда",
  "run.outputs": "Выходы",

  "assets.title": "Библиотека",
  "assets.sub": "Все загруженные файлы и результаты нод. ID ассета можно передать на вход конвейера.",
  "assets.allKinds": "Все типы",
  "assets.search": "Поиск по имени…",
  "assets.drop": "Перетащите сюда изображения, видео или модели, или",
  "assets.browse": "выберите",
  "assets.orImport": "или импортируйте",
  "assets.import": "Импорт",
  "assets.empty": "Здесь пока пусто.",
  "assets.uploaded": "Загружено файлов: {n}",
  "assets.confirmDelete": "Удалить {name} навсегда?",
  "assets.copyId": "Копировать ID",
  "assets.idCopied": "ID ассета скопирован",
  "assets.copyPath": "Копировать локальный путь",
  "assets.file": "файл: {kind}",
  "assets.path": "путь",
  "assets.copyPathHint": "Копировать локальный путь",
  "kind.image": "изображения", "kind.video": "видео", "kind.model": "3D-модели", "kind.audio": "аудио", "kind.json": "JSON", "kind.file": "файлы",

  "picker.forOutput": "Подключить выход {port} к…",
  "picker.forInput": "Подать на вход {port} из…",
  "picker.search": "Поиск нод…",
  "picker.empty": "Сюда нечего подключить",
  "node.textPlaceholder": "Введите значение…",
  "node.assetPlaceholder": "ID ассета, URL или путь",
  "node.dropHint": "Перетащите файл сюда или загрузите",
  "preview.empty": "Запустите конвейер — результат появится здесь.",
  "preview.expand": "Открыть во весь экран",
  "preview.download": "Скачать",
  "preview.partial": "Показано {shown} из {total} — остальные на выходе {port}[].",
  "preview.showAll": "Показать все",

  "param.auto": "(авто: первый доступный)",
  "param.noKey": "нет ключа",
  "param.missing": "не найден",
  "param.noMcp": "MCP-серверов пока нет — добавьте их в разделе «Модели и инструменты».",

  "settings.title": "Модели и инструменты",
  "settings.sub": "Подключайте нейросети (облачные API, локальные серверы, ComfyUI), MCP-серверы для агентов и свои ноды.",
  "settings.providers": "Провайдеры моделей",
  "settings.addProvider": "+ Добавить провайдера",
  "settings.colType": "Тип",
  "settings.colEndpoint": "Адрес",
  "settings.colModel": "Модель по умолчанию",
  "settings.colKey": "Ключ",
  "settings.keyReady": "готов",
  "settings.keyNotNeeded": "не нужен",
  "settings.keyMissing": "нет",
  "settings.default": "по умолчанию",
  "settings.confirmRemoveProvider": "Убрать провайдера {name}? Ноды, которые ссылаются на него по имени, перестанут работать.",
  "settings.providersHelp": "Ключ можно указать напрямую или как env:ИМЯ_ПЕРЕМЕННОЙ (читается из окружения сервера и не сохраняется). Локальные LLM: любой OpenAI-совместимый сервер — Ollama, LM Studio, vLLM.",
  "settings.editTitle": "Изменить: {name}",
  "settings.addProviderTitle": "Новый провайдер",
  "settings.nameHelp": "Ноды ссылаются на провайдера по этому имени.",
  "settings.baseUrl": "Базовый URL",
  "settings.defaultIs": "По умолчанию: {url}",
  "settings.apiKey": "API-ключ",
  "settings.apiKeyHelp": "Ключ или env:ПЕРЕМЕННАЯ. Рекомендуется: {hint}",
  "settings.extra": "Дополнительно (JSON)",
  "settings.extraAnthropic": "{\"fallbacks\": false} отключает серверный fallback при отказе модели.",
  "settings.mcp": "MCP-серверы (инструменты агентов)",
  "settings.addMcp": "+ Добавить MCP-сервер",
  "settings.colTransport": "Транспорт",
  "settings.colCommand": "Команда / URL",
  "settings.test": "Проверить",
  "settings.mcpHelp": "Blender: установите аддон blender-mcp в Blender, запустите его сервер в боковой панели и убедитесь, что uvx (из uv) есть в PATH. Unity: любой MCP-мост для Unity с HTTP-адресом или stdio-командой.",
  "settings.addMcpTitle": "Новый MCP-сервер",
  "settings.transport": "Транспорт",
  "settings.stdio": "stdio (локальная команда)",
  "settings.http": "Streamable HTTP",
  "settings.command": "Команда",
  "settings.commandHelp": "например uvx, npx, python, node или полный путь",
  "settings.args": "Аргументы (по одному в строке)",
  "settings.cwd": "Рабочая папка",
  "settings.env": "Переменные окружения (JSON)",
  "settings.envHelp": "Значения могут быть env:ПЕРЕМЕННАЯ, например {\"BLENDER_HOST\":\"localhost\"}",
  "settings.testTitle": "Проверка: {name}",
  "settings.connecting": "Подключение…",
  "settings.connected": "подключено",
  "settings.tools": "инструментов: {n}",
  "settings.custom": "Свои ноды (ваши нейросети)",
  "settings.newNode": "+ Новая нода",
  "settings.customEmpty": "Оберните любой HTTP-эндпоинт в переиспользуемую ноду со своими входами и настройками. Поддерживаются асинхронные задачи (опрос до готовности) и скачивание файлов-результатов.",
  "settings.customTitle": "Описание своей ноды",
  "settings.customHelp": "Inputs становятся портами, params — настройками ({{params.x}}). Секция http принимает все настройки ноды HTTP-запроса.",
  "settings.saveNode": "Сохранить ноду",
  "settings.nodeSaved": "Нода сохранена — она уже в палитре редактора",
  "settings.integration": "Использование из агентов и скриптов",
  "settings.snipMcp": "Подключить ForgeFlow к Claude Code как MCP-сервер",
  "settings.snipRun": "Запустить конвейер через REST и дождаться результата",
  "settings.snipNode": "Запустить одну ноду как инструмент",
  "settings.browserKey": "API-ключ для этого браузера",
  "settings.browserKeyHelp": "Нужен, только если на сервере задан ForgeFlow:ApiKey в appsettings.json.",
  "settings.openapi": "Спецификация OpenAPI:",
};

const dictionaries: Record<Lang, Record<MsgKey, string>> = { en, ru };
const storageKey = "forgeflow.lang";

function initialLang(): Lang {
  try {
    const saved = localStorage.getItem(storageKey);
    if (saved === "en" || saved === "ru") return saved;
  } catch { /* storage blocked */ }
  return navigator.language?.toLowerCase().startsWith("ru") ? "ru" : "en";
}

interface I18n {
  lang: Lang;
  setLang: (l: Lang) => void;
  t: (key: MsgKey, vars?: Record<string, string | number>) => string;
  ago: (iso: string) => string;
  spec: (s: NodeSpec) => NodeSpec;
  category: (c: string) => string;
  template: <T extends { id: string; name: string; description: string }>(t: T) => T;
}

const Ctx = createContext<I18n | null>(null);

export function LangProvider({ children }: { children: ReactNode }) {
  const [lang, setLangState] = useState<Lang>(initialLang);

  const setLang = useCallback((l: Lang) => {
    setLangState(l);
    try { localStorage.setItem(storageKey, l); } catch { /* storage blocked */ }
    document.documentElement.lang = l;
  }, []);

  const value = useMemo<I18n>(() => {
    const dict = dictionaries[lang];
    const t = (key: MsgKey, vars?: Record<string, string | number>) => {
      let s = dict[key] ?? en[key] ?? key;
      if (vars) for (const [k, v] of Object.entries(vars)) s = s.replaceAll(`{${k}}`, String(v));
      return s;
    };
    const ago = (iso: string) => {
      const sec = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
      if (sec < 60) return t("time.justNow");
      if (sec < 3600) return t("time.minAgo", { n: Math.floor(sec / 60) });
      if (sec < 86400) return t("time.hAgo", { n: Math.floor(sec / 3600) });
      return new Date(iso).toLocaleDateString(lang);
    };
    const category = (c: string) => (lang === "ru" ? categoryRu[c] ?? c : c);
    const spec = (s: NodeSpec): NodeSpec => {
      if (lang !== "ru") return s;
      const tr = nodeTextRu[s.type];
      return {
        ...s,
        title: tr?.title ?? s.title,
        description: tr?.description ?? s.description,
        category: category(s.category),
        outputs: s.outputs.map((p) => {
          const pt = p.label ? portTextRu[p.label] : undefined;
          return pt ? { ...p, label: pt.label ?? p.label, help: pt.help ?? p.help } : p;
        }),
        params: s.params.map((p) => {
          const pt = tr?.params?.[p.name] ?? paramTextRu[p.name];
          return { ...p, label: pt?.label ?? p.label, help: pt?.help === undefined ? p.help : pt.help || undefined };
        }),
      };
    };
    const template = <T extends { id: string; name: string; description: string }>(tpl: T): T => {
      const tr = lang === "ru" ? templateRu[tpl.id] : undefined;
      return tr ? { ...tpl, name: tr.name, description: tr.description } : tpl;
    };
    return { lang, setLang, t, ago, spec, category, template };
  }, [lang, setLang]);

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useI18n(): I18n {
  const v = useContext(Ctx);
  if (!v) throw new Error("useI18n outside LangProvider");
  return v;
}

export function LanguageSelect({ compact }: { compact?: boolean }) {
  const { lang, setLang, t } = useI18n();
  return (
    <select aria-label={t("common.language")} value={lang} onChange={(e) => setLang(e.target.value as Lang)}
      style={{ width: compact ? 110 : "100%" }}>
      {languages.map((l) => <option key={l.code} value={l.code}>{l.label}</option>)}
    </select>
  );
}
