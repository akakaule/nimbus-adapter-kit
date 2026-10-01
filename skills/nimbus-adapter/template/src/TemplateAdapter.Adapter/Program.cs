using TemplateAdapter.Adapter;

// The whole composition lives in AdapterComposition, so the tests build exactly what runs here.
var builder = Host.CreateApplicationBuilder(args);
builder.AddAdapter();
builder.Build().Run();
