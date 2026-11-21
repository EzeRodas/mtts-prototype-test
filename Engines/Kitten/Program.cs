using MoonTTS.Engine.Kitten;
using MoonTTS.Engine.Protocol;

/* KittenTTS out-of-process engine worker entrypoint */
return await EngineWorkerRunner.RunAsync(output => new KittenWorker(output));
