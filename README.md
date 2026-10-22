# ForgeFlow

ForgeFlow is a self-hosted platform for building game assets from node-based pipelines. You start from a prompt or a concept image and get a 3D model, an animation, sprites or UI, through any chain of neural networks you configure.

It combines ideas from two products:

- **AssetHub.io**: the node workflow and part-based characters. A character is split into parts, and any single part can be regenerated on its own.
- **Sorceress.games**: a wide set of tools and an HTTP API for agents.

On top of that, ForgeFlow adds:

- **Bring your own models.** Local LLMs (Ollama, LM Studio, vLLM), ComfyUI, your own HTTP servers, and cloud APIs (OpenAI, Claude, Tripo, Meshy, fal, Replicate).
- **Agent nodes.**
  - A local or cloud LLM drives Blender, Unity or other tools through **MCP**.
  - **Claude Code / Codex** run in your project folder as a pipeline step.
- **Fan-out.** A list output (for example "character parts" from an LLM) runs the next node once per item.
- **Rerun from any node.** Everything upstream is reused from the previous run.
- **Templates.** Reusable pipelines, plus your own nodes defined in JSON.
- **ForgeFlow as an MCP server.** Claude Code or Cursor can list and run your pipelines themselves.

## Quick start (Windows)

Requires .NET 10 SDK and Node.js 20+.

```bash
cd frontend
npm install
npm run build
cd ..
dotnet run --project backend/ForgeFlow.Api
```

Open http://localhost:5180 and run the **Offline demo** template. The interface is available in English and Russian (language switch in the sidebar, the editor toolbar and settings; the choice is remembered per browser). It needs no API keys and tests fan-out, the collage, the 9-slice slicer and the 3D viewer.

UI development with hot reload: run the backend as above, then `npm run dev` in `frontend/` and open http://localhost:5173.

Tests: `dotnet test` (29 tests). They include an end-to-end pipeline run and an agent loop against a real stdio MCP process.

## Connecting your neural networks

Go to **Models & tools → Model providers**. Keys can be literal or `env:VAR_NAME`. Using `env:` is recommended: the key stays in your environment and is not written to the database.

| Type | Used by | Examples |
|---|---|---|
| `openai` | LLM, Agent, Generate image | OpenAI (`gpt-image-1`, `gpt-5`), **Ollama** `http://localhost:11434/v1`, **LM Studio** `http://localhost:1234/v1`, vLLM, OpenRouter |
| `anthropic` | LLM, Agent | Claude through the official Anthropic SDK (`claude-opus-5-5`, server-side refusal fallback on by default; `{"fallbacks": false}` in Extra disables it) |
| `tripo` | Generate 3D, Tripo task | image/text/multi-view → 3D, `animate_rig`, `animate_retarget`, `convert_model` |
| `meshy` | Generate 3D | image-to-3d, text-to-3d |
| `fal` / `replicate` | Generate image / video / 3D | Kling, Wan, Seedance, Flux, TRELLIS, Hunyuan3D… any model id |
| `comfyui` | ComfyUI workflow | your local ComfyUI with any workflow exported as **API format** |
| `http` | HTTP request, custom nodes | your own inference server |

You can connect your own model in three ways:

1. **HTTP request node.** Set the URL, a body template (`{"prompt": "{{inputs.prompt | escape}}", "image": "{{inputs.image | datauri}}"}`), and optionally polling (`Poll URL = …/jobs/{{response.id}}`, `Done when = status=completed`). File URLs or local paths in the response become assets.
2. **Custom node.** In **Models & tools → Custom nodes**, save the same settings as JSON with named inputs and params. The node then appears in the palette like a built-in one. The files live in `backend/ForgeFlow.Api/storage/nodes/*.json`.
3. **ComfyUI.** Paste a workflow in API format and reference inputs as `{{comfy.image}}` and `{{inputs.prompt | escape}}`.

## Templates

Templates live in `backend/ForgeFlow.Api/templates`. You can add your own to `storage/templates`.

1. **Prompt → animated 3D character.** GPT Image concept → video model animates it → Tripo builds the mesh → **local GPT + Blender MCP** imports, rigs and animates it from the video frames → animated GLB.
2. **Prompt → moodboard → Unity UI (9-slice).**
   - An LLM writes the shots and images are generated per shot (fan-out), then collaged into a moodboard.
   - A UI-kit sheet is generated in the same style and auto-sliced into elements, with **estimated 9-slice borders**.
   - **Claude Code** runs inside your Unity project. It imports the sprites, sets `spriteBorder` in the `.meta` files, and builds Image prefabs with Image Type = Sliced plus a demo canvas.
3. **Concept → modular character by parts (AssetHub-style).** A vision LLM lists the parts → one image and one Tripo mesh per part → a Blender agent assembles them. To regenerate one part, use **Rerun from this node**.
4. **Image → rigged walking model (Tripo only).**
5. **Offline demo.** Mock nodes only.

### What each template needs locally

- **Blender MCP**
  1. Install `uv` (it provides `uvx`): https://docs.astral.sh/uv/
  2. Install the [blender-mcp](https://github.com/ahujasid/blender-mcp) add-on in Blender, then click "Connect to MCP server" in the Blender sidebar.
  3. In Settings, use **Test** on the "Blender MCP" server. It should list Blender's tools.
- **Local GPT**
  1. Run `ollama pull gpt-oss:20b`, or use any model with tool calling.
  2. The seeded provider "Local LLM (Ollama)" already points to `http://localhost:11434/v1`.
- **Claude Code**
  1. Install it with `npm install -g @anthropic-ai/claude-code` and log in once (`claude`).
  2. The **Agent CLI** node runs `claude -p --output-format json --permission-mode acceptEdits` in the working folder you set and sends the task on stdin.
  3. Files matching **Collect** become outputs.
- **ffmpeg** must be on PATH for the "Video → frames" node.

## Templating cheat sheet

Templates work in any text parameter:

- `{{inputs.<port>}}` — a value from a connected input. For an asset (file), this gives its local path.
- `{{inputs.<port>.<field>}}` — a field of a JSON value, for example `{{inputs.prompt.description}}` for an LLM list item.
- `{{workflow.<name>}}` — a pipeline input, for example `{{workflow.unityProject}}`.
- `{{response.id}}`, `{{poll.status}}` — values from the HTTP node's response and poll.
- Filters:

| Filter | Result |
|---|---|
| `escape` | escaped for a JSON string |
| `json` | the value as JSON |
| `datauri` | the file as base64 |
| `url` | the file's URL |
| `path` | the file's local path |
| `name` | the file name |
| `first`, `last` | the first or last list item |
| `join:", "` | list items joined with the separator |
| `lines` | a bullet list |
| `default:"x"` | a fallback for an empty value |

## Automation & agents

- **REST** — the full OpenAPI spec is at `/openapi/v1.json`.
  - `POST /api/workflows/{id}/run` with body `{"inputs": {...}, "wait": true}` runs a pipeline.
  - `POST /api/nodes/{type}/run` runs one node as a tool. A file input is passed as `{"$asset": "ast_…"}`.
  - `POST /api/runs/{id}/rerun` with body `{"fromNodeId": "…"}` reruns from a node.
  - `GET /api/runs/{id}/events` streams progress over SSE.
- **MCP** — `claude mcp add --transport http forgeflow http://localhost:5180/mcp`. This gives the tools `list_pipelines`, `run_pipeline`, `get_run`, `list_node_types` and `run_node`.
- **Security** — set `ForgeFlow:ApiKey` in `appsettings.json` to require `X-Api-Key` on `/api` and `/mcp`. The server listens on localhost only by default.
  - The **Agent CLI** node runs local commands, and Claude Code edits files in the folder you give it. Only expose the server to people you trust.

## Layout

```
backend/ForgeFlow.Api      ASP.NET Core 10 (C#)
  Engine/                  DAG scheduler, fan-out, retries, rerun-from-node, templates, SSE events
  Nodes/Builtin/           LLM, image/video/3D generation, image tools (collage, slice + 9-slice), HTTP, ComfyUI, agents
  Providers/               OpenAI-compatible, Anthropic SDK, Tripo, Meshy, fal, Replicate, ComfyUI clients
  Agents/                  MCP client (stdio + Streamable HTTP), command resolver
  Api/                     REST endpoints, ForgeFlow MCP server
  templates/               built-in pipeline templates
backend/ForgeFlow.Tests    xUnit: unit + end-to-end + agent loop
frontend/                  Vite + React + TypeScript, React Flow editor, model-viewer for GLB
```

Data (SQLite database, files, run folders, custom nodes) is stored in `backend/ForgeFlow.Api/storage/`. To change the location, set `ForgeFlow:DataDir`.
