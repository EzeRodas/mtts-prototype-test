using MoonTTS.Engine.Mbrola;
using MoonTTS.Engine.Protocol;

/* Entry point of the MBROLA engine worker */
return await EngineWorkerRunner.RunAsync(output => new MbrolaWorker(output));
