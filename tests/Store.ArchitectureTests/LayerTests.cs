using System.Reflection;
using NetArchTest.Rules;

namespace Store.ArchitectureTests;

/// <summary>Guards the clean-architecture dependency rule: dependencies point inwards only.</summary>
public class LayerTests
{
    private static readonly Assembly Domain = typeof(Store.Domain.Common.Entity).Assembly;
    private static readonly Assembly Application = typeof(Store.Application.DependencyInjection).Assembly;
    private static readonly Assembly Infrastructure = typeof(Store.Infrastructure.DependencyInjection).Assembly;

    [Fact]
    public void Domain_DependsOnNothing()
    {
        var result = Types.InAssembly(Domain).ShouldNot()
            .HaveDependencyOnAny("Store.Application", "Store.Infrastructure", "Store.Api", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Failing(result));
    }

    [Fact]
    public void Application_DoesNotDependOnInfrastructureOrWeb()
    {
        var result = Types.InAssembly(Application).ShouldNot()
            .HaveDependencyOnAny("Store.Infrastructure", "Store.Api", "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore.SqlServer")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Failing(result));
    }

    [Fact]
    public void Infrastructure_DoesNotDependOnApi()
    {
        var result = Types.InAssembly(Infrastructure).ShouldNot().HaveDependencyOn("Store.Api").GetResult();

        result.IsSuccessful.ShouldBeTrue(Failing(result));
    }

    [Fact]
    public void Controllers_AreThin_AndOnlyTalkToTheApplicationLayer()
    {
        var controllers = Types.InAssembly(typeof(Store.Api.Common.ApiControllerBase).Assembly)
            .That().ResideInNamespaceStartingWith("Store.Api.Controllers");

        var inherit = controllers.Should().Inherit(typeof(Store.Api.Common.ApiControllerBase)).And().BeSealed().GetResult();
        inherit.IsSuccessful.ShouldBeTrue(Failing(inherit));

        // Sign-in (including the external-provider handshake) goes through IIdentityService, never Identity or the auth handlers directly.
        var dependencies = Types.InAssembly(typeof(Store.Api.Common.ApiControllerBase).Assembly)
            .That().ResideInNamespaceStartingWith("Store.Api.Controllers")
            .ShouldNot().HaveDependencyOnAny("Store.Infrastructure", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore.Identity", "Microsoft.AspNetCore.Authentication")
            .GetResult();
        dependencies.IsSuccessful.ShouldBeTrue(Failing(dependencies));
    }

    [Fact]
    public void Handlers_AreInternalAndSealed()
    {
        var result = Types.InAssembly(Application)
            .That().HaveNameEndingWith("Handler")
            .Should().BeSealed().And().NotBePublic()
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Failing(result));
    }

    [Fact]
    public void DomainEntities_HaveNoPublicSetters()
    {
        var offenders = Domain.GetTypes()
            .Where(t => t.IsClass && t.Namespace?.StartsWith("Store.Domain", StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetProperties().Where(p => p.SetMethod?.IsPublic == true && !IsInitOnly(p)).Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    private static bool IsInitOnly(PropertyInfo property) =>
        property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit));

    private static string Failing(TestResult result) =>
        "Violations: " + string.Join(", ", result.FailingTypeNames ?? []);
}
