var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("bunting-pg-data")
    .WithPgAdmin();
var db = postgres.AddDatabase("bunting");

var server = builder.AddProject<Projects.Bunting_Server>("server")
    .WithReference(db)
    .WaitFor(db)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.Bunting_Admin>("admin")
    .WithReference(server)
    .WaitFor(server)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

builder.Build().Run();
