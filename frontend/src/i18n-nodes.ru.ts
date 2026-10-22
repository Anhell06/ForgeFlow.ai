// Russian texts for node specs and templates that come from the backend in English.
// Port names are intentionally not translated: templates reference them as {{inputs.<port>}}.

type ParamText = { label?: string; help?: string };
type NodeText = { title: string; description: string; params?: Record<string, ParamText> };

export const categoryRu: Record<string, string> = {
  Input: "Ввод",
  Output: "Вывод",
  Text: "Текст",
  Debug: "Отладка",
  Image: "Изображения",
  Video: "Видео",
  Integrations: "Интеграции",
  Custom: "Свои",
  "AI · Text": "ИИ · Текст",
  "AI · Image": "ИИ · Изображения",
  "AI · Video": "ИИ · Видео",
  "AI · 3D": "ИИ · 3D",
  "AI · Agents": "ИИ · Агенты",
};

/** Shared param texts by param name; node entries override them. */
export const paramTextRu: Record<string, ParamText> = {
  provider: { label: "Провайдер", help: "Пусто — первый настроенный провайдер подходящего типа." },
  model: { label: "Модель" },
  prompt: { label: "Промпт", help: "Шаблон: {{inputs.x}}, {{workflow.x}}, {{inputs.x | escape}}, …" },
  system: { label: "Системный промпт" },
  timeoutMin: { label: "Таймаут (мин)" },
  extra: { label: "Доп. поля", help: "Добавляются в тело запроса" },
  input: { label: "Входной JSON" },
  tolerance: { label: "Допуск фона (0–255)" },
  name: { label: "Имя" },
};

export const nodeTextRu: Record<string, NodeText> = {
  "input.text": {
    title: "Текстовый вход",
    description: "Вход конвейера (например, основной промпт). Его имя становится полем в окне запуска и в API.",
    params: { name: { label: "Имя входа" }, value: { label: "Значение по умолчанию" } },
  },
  "input.asset": {
    title: "Файловый вход",
    description: "Файл из библиотеки (изображение, модель, видео…) или URL. Можно переопределить при запуске по имени.",
    params: { name: { label: "Имя входа" }, asset: { label: "ID ассета или URL" } },
  },
  "text.template": {
    title: "Текстовый шаблон",
    description: "Собирает текст по шаблону. Входы — {{inputs.a}}, входы конвейера — {{workflow.prompt}}.",
    params: { template: { label: "Шаблон" } },
  },
  "text.split": {
    title: "Разбить на список",
    description: "Превращает текст (по элементу в строке или JSON-массив) в список. Следующие ноды выполнятся для каждого элемента.",
    params: { separator: { label: "Разделитель", help: "\\n — по строкам" } },
  },
  "output.result": {
    title: "Результат",
    description: "Помечает результат конвейера. Результаты возвращаются через API и показываются на странице запуска.",
    params: { name: { label: "Имя результата" } },
  },
  "output.preview": {
    title: "Просмотр",
    description: "Показывает результат прямо на холсте: изображения, видео, 3D-модели, аудио, текст, JSON или их списки. Подключите несколько выходов, чтобы сравнить их. Тоже попадает в результаты запуска.",
    params: { name: { label: "Имя результата", help: "Пусто — заголовок ноды" } },
  },
  "debug.mock": {
    title: "Mock-генератор",
    description: "Фейковый генератор для проверки конвейеров без сети: пишет градиентную картинку, куб .glb или повторяет текст.",
    params: { kind: { label: "Тип результата" }, delayMs: { label: "Задержка (мс)" }, fail: { label: "Специально упасть" } },
  },
  "llm.chat": {
    title: "LLM",
    description: "Запрос к чат-модели (GPT, Claude, локальная модель в Ollama/LM Studio). Видит изображения. Выход «items» — список (из JSON-массива или по строке на элемент); подключите его, чтобы запустить следующую ноду для каждого элемента.",
    params: {
      model: { label: "Модель", help: "Пусто — модель провайдера по умолчанию (например gpt-5, claude-opus-5-5, gpt-oss:20b)" },
      format: { label: "Формат ответа" },
      temperature: { label: "Температура", help: "Пусто — по умолчанию модели. Некоторые модели её не принимают." },
    },
  },
  "image.generate": {
    title: "Генерация изображения",
    description: "Текст (+ необязательные референсы) → изображение. OpenAI GPT Image или любая модель изображений на fal / Replicate.",
    params: {
      model: { label: "Модель", help: "OpenAI: gpt-image-1 … · fal: fal-ai/flux/dev … · Replicate: black-forest-labs/flux-schnell" },
      size: { label: "Размер", help: "OpenAI: 1024x1024, 1536x1024, 1024x1536, auto" },
      count: { label: "Количество" },
      quality: { label: "Качество", help: "OpenAI: low | medium | high" },
      input: { label: "Входной JSON (fal/Replicate)", help: "Заменяет стандартный {prompt, image_url}. Можно шаблоны: {{inputs.reference | first | datauri}}" },
    },
  },
  "video.generate": {
    title: "Генерация видео / анимации",
    description: "Изображение + промпт → видеоролик (Kling, Wan, Seedance, Hailuo… на fal или Replicate). Используйте, чтобы анимировать персонажа перед 3D.",
    params: {
      model: { label: "Модель", help: "Любой id image-to-video модели провайдера" },
      duration: { label: "Длительность (с)" },
      input: { label: "Входной JSON", help: "Заменяет стандартный {prompt, image_url, duration}. Пример: {\"prompt\":\"{{inputs.prompt | escape}}\",\"image_url\":\"{{inputs.image | datauri}}\"}" },
      extra: { label: "Доп. поля", help: "Добавляются во входной JSON" },
    },
  },
  "model.generate3d": {
    title: "Генерация 3D-модели",
    description: "Изображение, несколько ракурсов или текст → текстурированная 3D-модель (.glb). Tripo, Meshy или TRELLIS / Hunyuan3D / Rodin на fal и Replicate.",
    params: {
      mode: { label: "Режим" },
      model: { label: "Модель", help: "id модели fal/Replicate, например fal-ai/trellis, fal-ai/hunyuan3d/v2. Для Tripo: model_version в опциях." },
      options: { label: "Опции (JSON)", help: "Поля тела Tripo/Meshy, например {\"texture\":true,\"pbr\":true,\"face_limit\":20000}" },
      input: { label: "Входной JSON (fal/Replicate)", help: "Заменяет стандартный {image_url}" },
      extra: { label: "Доп. поля (fal/Replicate)" },
    },
  },
  "tripo.task": {
    title: "Задача Tripo (риг / анимация / конвертация)",
    description: "Любая задача Tripo над ранее созданной моделью: animate_rig, animate_retarget (preset:walk, preset:run…), convert_model, texture_model…",
    params: { body: { label: "JSON задачи", help: "Можно шаблоны. Риг: {\"type\":\"animate_rig\",\"original_model_task_id\":\"{{inputs.meta.taskId}}\",\"out_format\":\"glb\"}" } },
  },
  "video.frames": {
    title: "Видео → кадры",
    description: "Извлекает кадры через ffmpeg (должен быть в PATH). Удобно, чтобы показать анимацию vision-модели или агенту.",
    params: { fps: { label: "Кадров в секунду" }, maxFrames: { label: "Макс. кадров" }, ffmpeg: { label: "Путь к ffmpeg" } },
  },
  "image.collage": {
    title: "Коллаж / мудборд",
    description: "Раскладывает изображения по сетке — мудборд из нескольких генераций или спрайт-лист из кадров.",
    params: {
      columns: { label: "Колонки (0 = авто)" },
      cell: { label: "Размер ячейки (px)" },
      gap: { label: "Отступ (px)" },
      background: { label: "Фон", help: "Hex-цвет или transparent" },
    },
  },
  "image.removeBackground": {
    title: "Убрать однотонный фон",
    description: "Делает однотонный фон прозрачным (заливка от краёв). Для фото используйте ИИ-модель через fal/Replicate/ComfyUI.",
  },
  "image.slice": {
    title: "Нарезка на элементы (+9-slice)",
    description: "Режет изображение на части: фиксированной сеткой или автоматически по фону (UI-листы, мудборды, наборы иконок). Для каждой части оценивает 9-slice границы (left/top/right/bottom) — там, где середина перестаёт меняться.",
    params: {
      mode: { label: "Режим" },
      rows: { label: "Строк сетки" },
      columns: { label: "Колонок сетки" },
      tolerance: { label: "Допуск фона" },
      minSize: { label: "Мин. размер элемента (px)" },
      padding: { label: "Поля вокруг элемента (px)" },
      transparent: { label: "Сделать фон прозрачным" },
    },
  },
  "http.request": {
    title: "HTTP-запрос (ваша модель)",
    description: "Вызывает ваш сервер нейросети или любой REST API. Шаблоны: {{inputs.x}}, {{inputs.image | datauri}}, {{inputs.image | path}}. Для асинхронных API укажите URL опроса с {{response.id}}.",
    params: {
      provider: { label: "Провайдер (необязательно)", help: "Даёт шаблонам {{provider.baseUrl}} и {{provider.apiKey}}" },
      method: { label: "Метод" },
      url: { label: "URL" },
      headers: { label: "Заголовки (JSON)", help: "например {\"Authorization\":\"Bearer {{provider.apiKey}}\"}" },
      bodyMode: { label: "Тело" },
      body: { label: "Шаблон тела", help: "JSON с шаблонами. Для multipart: JSON-объект текстовых полей." },
      files: { label: "Файлы multipart", help: "По одному в строке: поле=входнойПорт, например image=image" },
      responseMode: { label: "Ответ" },
      pollUrl: { label: "URL опроса (асинхронные задачи)", help: "например http://localhost:8000/jobs/{{response.id}} — пусто = без опроса" },
      doneWhen: { label: "Готово, когда", help: "путь=значение[|значение2] в ответе опроса" },
      failWhen: { label: "Ошибка, когда" },
      resultUrl: { label: "URL результата", help: "Необязательный финальный GET после опроса, например …/jobs/{{response.id}}/result" },
      resultPath: { label: "Путь к результату", help: "Путь через точку к нужной части итогового JSON, например output.files" },
      download: { label: "Скачать файлы по URL из результата" },
    },
  },
  "comfyui.workflow": {
    title: "Workflow ComfyUI",
    description: "Запускает workflow ComfyUI в API-формате (Workflow → Export (API)). Подключённые изображения сначала загружаются; ссылайтесь на них как {{comfy.image}}, на текст — {{inputs.prompt | escape}}. Подходит для изображений, видео и 3D (Hunyuan3D, TRELLIS).",
    params: {
      workflow: { label: "Workflow (API JSON)" },
      seed: { label: "Seed", help: "Доступен как {{params.seed}}; пусто = случайный → {{comfy.seed}}" },
    },
  },
  "agent.mcp": {
    title: "Агент + MCP-инструменты",
    description: "LLM, которая использует MCP-серверы как инструменты — например, локальная GPT управляет Blender через Blender MCP: импортирует, ригает и анимирует модель. Агент получает пути входных файлов; файлы, сохранённые в выходную папку (или зарегистрированные), становятся выходами.",
    params: {
      model: { label: "Модель", help: "Модель с вызовом инструментов: gpt-5, claude-opus-5-5, gpt-oss:20b, qwen3…" },
      mcpServers: { label: "MCP-серверы", help: "Имена или id из раздела «Модели и инструменты → MCP-серверы»" },
      prompt: { label: "Задача" },
      maxSteps: { label: "Макс. шагов с инструментами" },
    },
  },
  "agent.cli": {
    title: "Агент CLI (Claude Code, Codex…)",
    description: "Запускает локальный CLI-агент в рабочей папке: Claude Code в вашем Unity-проекте, Codex в репозитории или любой скрипт. Задача передаётся через stdin вместе с путями входных файлов. Файлы по маскам «Собрать» (или сохранённые в выходную папку) становятся выходами.",
    params: {
      preset: { label: "Пресет" },
      command: { label: "Команда", help: "Пусто = команда пресета (claude / codex)" },
      args: { label: "Аргументы (по одному в строке)", help: "Пусто = аргументы пресета. Claude Code: -p --output-format json --permission-mode acceptEdits. Можно шаблоны." },
      workdir: { label: "Рабочая папка", help: "например D:/Unity/MyGame — агент запускается здесь" },
      prompt: { label: "Задача" },
      collect: { label: "Собрать файлы (маски относительно рабочей папки)", help: "например Assets/UI/Generated/**/*.png — берутся только файлы, изменённые во время запуска" },
      env: { label: "Переменные окружения (JSON)", help: "Дополнительные переменные; значения могут быть env:VAR" },
    },
  },
};

export const templateRu: Record<string, { name: string; description: string }> = {
  "prompt-to-animated-3d-blender": {
    name: "Промпт → анимированный 3D-персонаж (GPT → видео → Tripo → агент Blender)",
    description: "Промпт → концепт-арт в GPT Image → ИИ-видео движения → 3D-модель Tripo → локальная GPT управляет Blender через Blender MCP: импортирует модель, делает риг, воссоздаёт анимацию по кадрам видео и экспортирует анимированный GLB.",
  },
  "moodboard-to-unity-ui": {
    name: "Промпт → мудборд → UI в Unity с 9-slice (Claude Code)",
    description: "LLM пишет кадры мудборда, по ним генерируются изображения и собираются в мудборд. В том же стиле генерируется лист UI-элементов, автоматически режется с оценкой 9-slice границ, а локальный Claude Code импортирует всё в ваш Unity-проект: спрайты с границами, Sliced-префабы и демо-канвас.",
  },
  "concept-to-modular-character": {
    name: "Концепт → модульный персонаж по частям (как AssetHub)",
    description: "Vision-LLM делит концепт на части (тело, волосы, броня, ботинки…). Для каждой части — своё изолированное изображение и свой 3D-меш, поэтому любую часть можно перегенерировать отдельно через «Перезапустить с этой ноды». Агент в Blender собирает части в одного персонажа, готового к ригу.",
  },
  "image-to-rigged-walk-tripo": {
    name: "Изображение → модель с ригом и ходьбой (только Tripo)",
    description: "Самый быстрый облачный путь: Tripo image-to-3D, затем авториг Tripo и ретаргет на пресет ходьбы. Локальный софт не нужен.",
  },
  "offline-demo": {
    name: "Офлайн-демо (без API-ключей)",
    description: "Только mock-ноды — показывает fan-out по списку, коллаж, 3D-превью и нарезку 9-slice без трат кредитов. Используйте, чтобы проверить установку.",
  },
};
