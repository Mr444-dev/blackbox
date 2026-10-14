using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Blackbox.Api;

public sealed class ApiKeyOperationFilter : IOperationFilter
{
    public const string SchemeName = "ApiKey";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var allowsAnonymousAccess = context.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<IAllowAnonymous>()
            .Any();

        if (allowsAnonymousAccess)
            return;

        operation.Security ??= new List<OpenApiSecurityRequirement>();
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(SchemeName, context.Document)] = new List<string>()
        });
    }
}
