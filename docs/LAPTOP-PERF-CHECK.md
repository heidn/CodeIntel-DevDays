# Laptop Performance Diagnostic Brief

A self-contained checklist for diagnosing why LLamaSharp inference feels slow on a given Windows machine — primarily aimed at the work ZBook where CUDA was perceived as laggy. Hand this to a diagnostic agent (or run through it manually) before touching csproj, appsettings, or driver settings.

## Background

- The app is at `c:\Users\heidn\Repos\Devdays\CodeIntel`.
- Model loader: [src/CodeIntel.Server/Services/LlamaSharpService.cs](../src/CodeIntel.Server/Services/LlamaSharpService.cs), LLamaSharp 0.27, Qwen2.5-Coder-7B Q4_K_M GGUF.
- llama.cpp / LLamaSharp pick **exactly one** backend per process. Vulkan, CUDA, and CPU are mutually-exclusive native libraries; you cannot run a mixed Vulkan+CUDA+CPU pipeline. The only "hybrid" knob is `Llm:GpuLayerCount` — how many transformer layers offload to the chosen GPU backend (the rest run on CPU).
- The csproj today references `LLamaSharp.Backend.Cpu` + `LLamaSharp.Backend.Vulkan`. CUDA is commented out, so the work laptop may actually be running Vulkan-against-NVIDIA (functional, but slower than native CUDA).
- `GpuLayerCount = 0` in `appsettings.json` is **not** CPU-only — [LlamaSharpService.cs:139](../src/CodeIntel.Server/Services/LlamaSharpService.cs#L139) substitutes 20 when the value is 0 and Backend is Auto/Vulkan. A 7B Q4_K_M has 33 layers; 20/33 means every token still bounces over PCIe.

## What to collect (read-only — no modifications without confirmation)

1. **GPU + driver inventory**
   - `nvidia-smi`
   - `nvidia-smi -q | findstr "Driver CUDA"`
   - `Get-CimInstance Win32_VideoController | Select Name,DriverVersion,AdapterRAM`
   - Note CUDA runtime version the driver supports (top-right of `nvidia-smi`) and total VRAM. If both iGPU and dGPU exist, note both.

2. **Which backend is actually loaded**
   - Search recent app logs (stdout / Serilog console) for `"Backend:"` — the service logs `Backend: vulkan, GPU layers: N` or `Backend: cpu` on startup ([LlamaSharpService.cs:148,158](../src/CodeIntel.Server/Services/LlamaSharpService.cs#L148)).
   - Also check whatever the app surfaces via `/readyz`.

3. **Native libs on disk**
   - Under the app's bin/runtime folder, list `llama*.dll`, `ggml*.dll`, and any `cublas*` / `cudart*` / `vulkan*` DLLs.
   - Whichever set is present tells you what llama.cpp will actually load. Multiple `Backend.*` NuGet packages can ship conflicting natives — first-found wins.

4. **VRAM headroom during inference**
   - Run `nvidia-smi -l 1` while a small analysis run is kicked off.
   - Note VRAM used vs total, GPU utilization %, and whether power draw climbs to the card's TDP or stalls low.
   - VRAM near 100% with low utilization = spilling, which is **slower than CPU-only**.

5. **Power + thermal state**
   - `powercfg /getactivescheme` — should be High Performance or Ultimate, not Balanced.
   - NVIDIA Control Panel → Manage 3D Settings → app's "Power management mode" should be "Prefer maximum performance."
   - `nvidia-smi -q -d PERFORMANCE` for throttling reasons (`Thermal`, `Power`, `HW Slowdown`).

6. **Battery vs AC**
   - `WMIC Path Win32_Battery Get BatteryStatus` (2 = on AC).
   - Many laptops aggressively downclock dGPU on battery.

7. **Competing GPU users**
   - `nvidia-smi` process list.
   - Browsers, Teams, Discord, OBS, Windows Copilot, and Visual Studio's ML features can all hold VRAM.

8. **CPU side**
   - `Get-CimInstance Win32_Processor | Select Name,NumberOfCores,NumberOfLogicalProcessors`.
   - `Llm:Threads` is null (auto) by default — confirm what LLamaSharp ends up using.
   - Corporate AV / EDR (CrowdStrike, Defender for Endpoint) can crush per-token latency.

9. **Effective appsettings values**
   - Read `src/CodeIntel.Server/appsettings.json` and any `appsettings.Development.json` / env vars.
   - Specifically: `Llm:Backend`, `Llm:GpuLayerCount`, `Llm:ContextSize`, `Analysis:MaxContextTokens`.
   - Reminder: `GpuLayerCount = 0` defaults to 20 in code, not 0.

10. **Measure tokens/sec**
    - Time a single inference and divide by tokens emitted.
    - Under ~5 tok/s for a 7B Q4 on a modern dGPU = partial offload or PCIe stalls.
    - CPU-only on a modern i7 lands around 4–8 tok/s.
    - Full GPU offload (33/33 layers) should be 25–40+ on a decent dGPU.

## Report format

Return a short table:

| Field | Value |
|---|---|
| Backend in use | (vulkan / cuda / cpu) |
| GPU model | |
| VRAM total / used | |
| Layers offloaded | (N / 33) |
| Tokens/sec measured | |
| Power plan | |
| AC vs battery | |
| Throttling reasons | |
| Conflicting native DLLs | |
| Competing GPU processes | |

Flag the single most likely cause and a one-line fix. **Do not modify csproj, appsettings, or driver settings without confirmation.**

## Common fixes (only after diagnosis)

- **CUDA on NVIDIA dGPU instead of Vulkan**: swap `LLamaSharp.Backend.Vulkan` for `LLamaSharp.Backend.Cuda12` in [CodeIntel.Server.csproj](../src/CodeIntel.Server/CodeIntel.Server.csproj). Reference only one Backend.* package — multiple can confuse the native loader.
- **Partial offload**: set `Llm:GpuLayerCount = 33` (full offload for 7B) if VRAM ≥ 6 GB. Use `-1` to mean "all layers."
- **VRAM spill**: reduce `Llm:ContextSize` or `GpuLayerCount` until VRAM stays comfortably under 95%.
- **Power management**: switch to High Performance plan; in NVIDIA Control Panel set the app to "Prefer maximum performance."
- **AV interference**: add the app folder to corporate AV/EDR exclusions if policy permits.
