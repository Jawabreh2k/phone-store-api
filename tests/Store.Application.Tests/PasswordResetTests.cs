using System.Text.RegularExpressions;
using System.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Store.Application.Abstractions;
using Store.Application.Customers;
using Store.Domain.Common;
using Store.Infrastructure.Identity;

namespace Store.Application.Tests;

/// <summary>Forgot / reset password through the real handlers, ASP.NET Identity's tokens and the reset email.</summary>
public sealed class PasswordResetTests : IAsyncLifetime
{
    private const string Email = "layla@example.com";
    private const string OldPassword = "oldpassword1";
    private const string NewPassword = "newpassword2";

    private TestHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync(withSampleCatalog: false);
        await CreateUserAsync(Email, "Layla Haddad", Language.En);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task ForgotPassword_UnknownEmail_SucceedsWithoutSendingMail()
    {
        var result = await _host.Send(new ForgotPasswordCommand("nobody@example.com"));

        result.IsSuccess.ShouldBeTrue();
        _host.Emails.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task ForgotPassword_KnownEmail_MailsALinkToTheStorefrontResetPage()
    {
        (await _host.Send(new ForgotPasswordCommand(Email))).IsSuccess.ShouldBeTrue();

        var mail = _host.Emails.Sent.ShouldHaveSingleItem();
        mail.To.ShouldBe([Email]);
        mail.Subject.ShouldBe("Reset your PhoneStore password");
        mail.Body.ShouldStartWith("Hi Layla Haddad,");

        var link = ResetLink(mail);
        link.GetLeftPart(UriPartial.Path).ShouldBe($"{TestHost.StorefrontUrl}/en/reset-password");
        HttpUtility.ParseQueryString(link.Query)["email"].ShouldBe(Email);
        HttpUtility.ParseQueryString(link.Query)["token"].ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ForgotPassword_WritesTheMailInTheCustomersLanguage()
    {
        await CreateUserAsync("sara@example.com", "سارة", Language.Ar);

        (await _host.Send(new ForgotPasswordCommand("sara@example.com"))).IsSuccess.ShouldBeTrue();

        var mail = _host.Emails.Sent.ShouldHaveSingleItem();
        mail.Subject.ShouldBe("إعادة تعيين كلمة المرور في فون ستور");
        ResetLink(mail).AbsolutePath.ShouldBe("/ar/reset-password");
    }

    [Fact]
    public async Task ResetPassword_WithTheMailedToken_ChangesThePassword_OnlyOnce()
    {
        var token = await RequestResetTokenAsync();

        (await _host.Send(new ResetPasswordCommand(Email, token, NewPassword))).IsSuccess.ShouldBeTrue();
        (await PasswordIsAsync(NewPassword)).ShouldBeTrue();
        (await PasswordIsAsync(OldPassword)).ShouldBeFalse();

        // Resetting rotates the security stamp that every earlier token is bound to.
        var again = await _host.Send(new ResetPasswordCommand(Email, token, "anotherpass3"));
        again.Error.ShouldBe(IdentityErrors.InvalidResetToken);
        (await PasswordIsAsync(NewPassword)).ShouldBeTrue();
    }

    [Fact]
    public async Task ResetPassword_WithAnInvalidToken_FailsAndKeepsThePassword()
    {
        var token = await RequestResetTokenAsync();

        var tampered = (token[0] == 'A' ? "B" : "A") + token[1..];
        (await _host.Send(new ResetPasswordCommand(Email, tampered, NewPassword))).Error.ShouldBe(IdentityErrors.InvalidResetToken);
        (await _host.Send(new ResetPasswordCommand(Email, "not base64url!", NewPassword))).Error.ShouldBe(IdentityErrors.InvalidResetToken);

        // Another account's email gets the same answer, so the form reveals nothing about who has an account.
        (await _host.Send(new ResetPasswordCommand("nobody@example.com", token, NewPassword))).Error.ShouldBe(IdentityErrors.InvalidResetToken);

        (await PasswordIsAsync(OldPassword)).ShouldBeTrue();
    }

    [Fact]
    public async Task ResetPassword_AfterTheTokenLifespan_FailsAsExpired()
    {
        // Identity checks the lifespan against the system clock, not TimeProvider, so rather than waiting a day
        // this host gives tokens no lifespan at all: each one has expired by the time it is used.
        await _host.DisposeAsync();
        _host = await TestHost.CreateAsync(
            withSampleCatalog: false,
            configure: services => services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.Zero));
        await CreateUserAsync(Email, "Layla Haddad", Language.En);
        var token = await RequestResetTokenAsync();

        var result = await _host.Send(new ResetPasswordCommand(Email, token, NewPassword));

        result.Error.ShouldBe(IdentityErrors.InvalidResetToken);
        (await PasswordIsAsync(OldPassword)).ShouldBeTrue();
    }

    [Fact]
    public async Task ResetPassword_WeakPassword_ReportsThePolicy_AndTheTokenStillWorks()
    {
        var token = await RequestResetTokenAsync();

        // Long enough for the validator, but Identity's policy also wants a digit.
        (await _host.Send(new ResetPasswordCommand(Email, token, "nodigitshere"))).Error.ShouldBe(IdentityErrors.WeakPassword);
        (await _host.Send(new ResetPasswordCommand(Email, token, "short1"))).Error.ShouldBeOfType<ValidationError>();

        (await _host.Send(new ResetPasswordCommand(Email, token, NewPassword))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ResetPassword_LiftsALockoutFromFailedSignIns()
    {
        var lockedOut = await _host.Run<UserManager<ApplicationUser>, bool>(async users =>
        {
            var user = (await users.FindByEmailAsync(Email))!;
            for (var i = 0; i < 5; i++)
            {
                await users.AccessFailedAsync(user);
            }

            return await users.IsLockedOutAsync(user);
        });
        lockedOut.ShouldBeTrue();
        var token = await RequestResetTokenAsync();

        (await _host.Send(new ResetPasswordCommand(Email, token, NewPassword))).IsSuccess.ShouldBeTrue();

        lockedOut = await _host.Run<UserManager<ApplicationUser>, bool>(async users => await users.IsLockedOutAsync((await users.FindByEmailAsync(Email))!));
        lockedOut.ShouldBeFalse();
    }

    private async Task CreateUserAsync(string email, string fullName, Language language)
    {
        var created = await _host.Run<UserManager<ApplicationUser>, IdentityResult>(users => users.CreateAsync(
            new ApplicationUser { UserName = email, Email = email, FullName = fullName, PreferredLanguage = language },
            OldPassword));
        created.Succeeded.ShouldBeTrue();
    }

    private async Task<string> RequestResetTokenAsync()
    {
        (await _host.Send(new ForgotPasswordCommand(Email))).IsSuccess.ShouldBeTrue();
        return HttpUtility.ParseQueryString(ResetLink(_host.Emails.Sent[^1]).Query)["token"]!;
    }

    private static Uri ResetLink(EmailMessage mail) =>
        new(Regex.Match(mail.Body, $@"{Regex.Escape(TestHost.StorefrontUrl)}\S+").Value);

    private Task<bool> PasswordIsAsync(string password) =>
        _host.Run<UserManager<ApplicationUser>, bool>(async users => await users.CheckPasswordAsync((await users.FindByEmailAsync(Email))!, password));
}
