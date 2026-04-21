using QuickReply.CloudService;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddKnowledgeCloudServices(builder.Configuration);

var app = builder.Build();

await app.InitializeKnowledgeCloudAsync();

app.MapKnowledgeCloudEndpoints();

app.Run();

public partial class Program;
