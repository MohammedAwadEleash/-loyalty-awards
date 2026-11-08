using System.Text.Json.Serialization;
using LoyaltyAwards.Api;
using LoyaltyAwards.Api.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers(options =>
        // Report validation errors with the JSON names ("points") instead of C# names ("Points").
        options.ModelMetadataDetailsProviders.Add(new SystemTextJsonValidationMetadataProvider()))
    .AddJsonOptions(options =>
    {
        // The web default accepts "25" for a number; the assignment requires that to be a 400.
        options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        // Property names must match exactly ("sourceId", not "SOURCEID").
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = false;
    })
    .ConfigureApiBehaviorOptions(options =>
        // Invalid or malformed bodies return our ErrorResponse shape instead of the default ProblemDetails.
        options.InvalidModelStateResponseFactory = context =>
            new BadRequestObjectResult(new ErrorResponse(
                "invalid_request",
                "The request body is missing, malformed or has invalid fields.",
                ValidationErrors.From(context.ModelState))));

builder.Services.AddSingleton<LoyaltyStore>();

var app = builder.Build();

// Any unexpected failure still returns a JSON error object.
app.UseExceptionHandler(errorApp => errorApp.Run(context =>
    Results.Json(new ErrorResponse("internal_error", "An unexpected error occurred."), statusCode: 500)
        .ExecuteAsync(context)));

// Errors produced by routing (unknown path 404, wrong method 405) otherwise have no body.
app.UseStatusCodePages(context => Results.Json(
        new ErrorResponse(
            context.HttpContext.Response.StatusCode == 405 ? "method_not_allowed" : "not_found",
            context.HttpContext.Response.StatusCode == 405
                ? $"Method {context.HttpContext.Request.Method} is not allowed on {context.HttpContext.Request.Path}."
                : $"No endpoint matches {context.HttpContext.Request.Path}."),
        statusCode: context.HttpContext.Response.StatusCode)
    .ExecuteAsync(context.HttpContext));

app.MapControllers();

app.Run();

public partial class Program; // exposed for WebApplicationFactory in tests
