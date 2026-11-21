using MoonTTS.Engine.Kokoro;
using MoonTTS.Engine.Protocol;

/* Entry point of the Kokoro engine worker */
return await EngineWorkerRunner.RunAsync(output => new KokoroWorker(output));
