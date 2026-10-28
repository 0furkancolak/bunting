var builder = DistributedApplication.CreateBuilder(args);

// A fixed development password: with a random one, the persisted data volume rejects the next run's credentials.
var postgresPassword = builder.AddParameter("postgres-password", "bunting-dev-postgres", publishValueAsDefault: false, secret: true);
var postgres = builder.AddPostgres("postgres", password: postgresPassword)
    .WithDataVolume("bunting-pg-data")
    .WithPgAdmin();
var db = postgres.AddDatabase("bunting");

var server = builder.AddProject<Projects.Bunting_Server>("server")
    .WithReference(db)
    .WaitFor(db)
    // Development-only bootstrap token for the Management API. Never use this value outside local Aspire runs.
    .WithEnvironment("Bunting__Management__Tokens__0__Name", "dev-owner")
    .WithEnvironment("Bunting__Management__Tokens__0__Token", "dev-owner-token")
    .WithEnvironment("Bunting__Management__Tokens__0__Role", "Owner")
    // Development-only first administrator for the admin panel. Never use these values outside local Aspire runs.
    .WithEnvironment("Bunting__Bootstrap__AdminEmail", "admin@bunting.local")
    .WithEnvironment("Bunting__Bootstrap__AdminPassword", "dev-admin-password")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.Bunting_Admin>("admin")
    .WithReference(server)
    .WaitFor(server)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

builder.Build().Run();
