using ConsoleAppFramework;
using ThorstenSauter.Azure.Cosmos.Migrations.Tool;

var app = ConsoleApp.Create();
app.Add<Commands>();
await app.RunAsync(args);
