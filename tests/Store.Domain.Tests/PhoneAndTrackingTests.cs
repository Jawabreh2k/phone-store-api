using Store.Domain.Common;
using Store.Domain.Orders;

namespace Store.Domain.Tests;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("0791234567")]
    [InlineData("079 123 4567")]
    [InlineData("079-123-4567")]
    [InlineData("(079) 123.4567")]
    [InlineData("+962 79 123 4567")]
    [InlineData("+962 079 123 4567")]
    [InlineData("+9620791234567")]
    [InlineData("+962 (0) 79 123 4567")]
    [InlineData("00962 079 123 4567")]
    [InlineData("00962791234567")]
    [InlineData("962 079 123 4567")]
    [InlineData("9620791234567")]
    [InlineData("962791234567")]
    [InlineData("0962791234567")]
    [InlineData("791234567")]
    [InlineData("٠٧٩١٢٣٤٥٦٧")]   // Arabic-Indic digits from an Arabic keyboard
    [InlineData("۰۷۹۱۲۳۴۵۶۷")]   // Persian digits
    public void Create_AllCommonFormsOfTheSameNumber_AreEqual(string input)
    {
        PhoneNumber.Create(input, "962").Value.Value.ShouldBe("+962791234567");
    }

    [Fact]
    public void Create_KeepsForeignCountryCode()
    {
        PhoneNumber.Create("+966 50 123 4567", "962").Value.Value.ShouldBe("+966501234567");
        PhoneNumber.Create("00971501234567", "962").Value.Value.ShouldBe("+971501234567");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("079123456a")]
    [InlineData("12")]
    [InlineData("+0791234567")]
    [InlineData("1234567890123456789")]
    [InlineData("079+1234567")]
    public void Create_RejectsNonNumbers(string input)
    {
        PhoneNumber.Create(input, "962").Error.ShouldBe(PhoneNumber.Invalid);
    }

    [Theory]
    [InlineData("1234567")]          // became +9621234567 before the subscriber-length rule
    [InlineData("0123 4567")]
    [InlineData("+962 1234567")]
    [InlineData("00962 1234567")]
    [InlineData("962 0 1234567")]
    [InlineData("079 12345")]
    public void Create_RejectsTooShortNumbersForTheStoreCountry(string input)
    {
        PhoneNumber.Create(input, "962").Error.ShouldBe(PhoneNumber.Invalid);
    }

    [Fact]
    public void Create_AcceptsStoreCountryLandlinesAndShortForeignNumbers()
    {
        PhoneNumber.Create("06 123 4567", "962").Value.Value.ShouldBe("+96261234567");
        PhoneNumber.Create("+354 123 4567", "962").Value.Value.ShouldBe("+3541234567");
    }

    [Theory]
    [InlineData("079 123", "79123")]
    [InlineData("+962791", "962791")]
    [InlineData("07", null)]
    [InlineData("abc", null)]
    public void SearchDigits_DropsFormattingAndTrunkZero(string input, string? expected)
    {
        PhoneNumber.SearchDigits(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("+962 079 123 4567", "962791234567")]
    [InlineData("9620791234567", "962791234567")]
    [InlineData("0791234567", "791234567")]
    public void SearchDigits_DropsTrunkZeroAfterTheStoreCountryCode(string input, string expected)
    {
        PhoneNumber.SearchDigits(input, "962").ShouldBe(expected);
    }
}

public class OrderTrackingTokenTests
{
    [Fact]
    public void Generate_IsUrlSafeAndUnique()
    {
        var tokens = Enumerable.Range(0, 1000).Select(_ => OrderTrackingToken.Generate()).ToList();

        tokens.ShouldAllBe(t => OrderTrackingToken.LooksValid(t));
        tokens.Distinct().Count().ShouldBe(tokens.Count);
    }

    [Fact]
    public void Hash_IsStableAndNotTheToken()
    {
        var token = OrderTrackingToken.Generate();

        OrderTrackingToken.Hash(token).ShouldBe(OrderTrackingToken.Hash(token));
        OrderTrackingToken.Hash(token).Length.ShouldBe(OrderTrackingToken.HashLength);
        OrderTrackingToken.Hash(token).ShouldNotContain(token);
    }

    [Theory]
    [InlineData("100001")]
    [InlineData("short")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaa/")]
    public void LooksValid_RejectsOrderNumbersAndJunk(string candidate)
    {
        OrderTrackingToken.LooksValid(candidate).ShouldBeFalse();
    }
}
