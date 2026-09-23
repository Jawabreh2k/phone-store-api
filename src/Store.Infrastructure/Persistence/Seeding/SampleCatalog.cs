using Store.Domain.Catalog;
using Store.Domain.Common;

namespace Store.Infrastructure.Persistence.Seeding;

/// <summary>
/// Demo catalogue for development and client demos: a handful of phones and accessories with
/// realistic variants, specs and compatibility. There are no images; the storefront shows a placeholder.
/// </summary>
internal static class SampleCatalog
{
    private static readonly LocalizedText Black = new("Black", "أسود");
    private static readonly LocalizedText White = new("White", "أبيض");
    private static readonly LocalizedText Blue = new("Blue", "أزرق");
    private static readonly LocalizedText Titanium = new("Natural Titanium", "تيتانيوم طبيعي");
    private static readonly LocalizedText Green = new("Mint", "نعناعي");

    public static async Task SeedAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var apple = new Brand("Apple", "apple", sortOrder: 1);
        var samsung = new Brand("Samsung", "samsung", sortOrder: 2);
        var xiaomi = new Brand("Xiaomi", "xiaomi", sortOrder: 3);
        var anker = new Brand("Anker", "anker", sortOrder: 4);
        db.Brands.AddRange(apple, samsung, xiaomi, anker);

        var smartphones = new Category(new("Smartphones", "الهواتف الذكية"), "smartphones", ProductType.Phone, 1);
        var cases = new Category(new("Cases", "الأغطية"), "cases", ProductType.Accessory, 1);
        var chargers = new Category(new("Chargers & Cables", "الشواحن والكوابل"), "chargers", ProductType.Accessory, 2);
        var protectors = new Category(new("Screen Protectors", "واقيات الشاشة"), "screen-protectors", ProductType.Accessory, 3);
        var audio = new Category(new("Audio", "السماعات"), "audio", ProductType.Accessory, 4);
        db.Categories.AddRange(smartphones, cases, chargers, protectors, audio);

        var iphone = Phone(apple, smartphones, "iPhone 16 Pro", "آيفون 16 برو", "iphone-16-pro",
            "Titanium design, A18 Pro chip and a 48MP camera system.",
            "تصميم من التيتانيوم، شريحة A18 Pro ونظام كاميرا 48 ميجابكسل.",
            featured: true,
            [("IP16P-128-TI", Titanium, "#BFB6A8", 128, 8, 899m, null, 12),
             ("IP16P-256-TI", Titanium, "#BFB6A8", 256, 8, 999m, null, 7),
             ("IP16P-256-BK", Black, "#3B3B3D", 256, 8, 999m, null, 3),
             ("IP16P-512-BK", Black, "#3B3B3D", 512, 8, 1199m, null, 0)],
            [("Display", "الشاشة", "6.3\" Super Retina XDR OLED, 120Hz", "6.3 إنش OLED بمعدل تحديث 120 هرتز"),
             ("Chip", "المعالج", "A18 Pro", "A18 Pro"),
             ("Main camera", "الكاميرا الرئيسية", "48MP + 48MP ultra-wide + 12MP 5x telephoto", "48 + 48 واسعة + 12 ميجابكسل تقريب 5x"),
             ("Battery", "البطارية", "Up to 27 hours video playback", "حتى 27 ساعة تشغيل فيديو")]);

        var galaxy = Phone(samsung, smartphones, "Galaxy S24", "جالاكسي S24", "galaxy-s24",
            "Compact flagship with Galaxy AI and a bright 120Hz display.",
            "هاتف رائد بحجم مدمج مع ميزات Galaxy AI وشاشة ساطعة بتردد 120 هرتز.",
            featured: true,
            [("GS24-128-BK", Black, "#1F1F1F", 128, 8, 649m, 749m, 20),
             ("GS24-256-BK", Black, "#1F1F1F", 256, 8, 699m, 799m, 9),
             ("GS24-256-BL", Blue, "#9DB4D6", 256, 8, 699m, 799m, 4)],
            [("Display", "الشاشة", "6.2\" Dynamic AMOLED 2X, 120Hz", "6.2 إنش Dynamic AMOLED 2X بتردد 120 هرتز"),
             ("Chip", "المعالج", "Exynos 2400", "Exynos 2400"),
             ("Battery", "البطارية", "4000 mAh, 25W charging", "4000 مللي أمبير، شحن 25 واط")]);

        var redmi = Phone(xiaomi, smartphones, "Redmi Note 13", "ريدمي نوت 13", "redmi-note-13",
            "Great value with a 108MP camera and AMOLED display.",
            "قيمة ممتازة مع كاميرا 108 ميجابكسل وشاشة AMOLED.",
            featured: false,
            [("RN13-128-BK", Black, "#222222", 128, 6, 179m, null, 30),
             ("RN13-256-GR", Green, "#A8D5BA", 256, 8, 209m, null, 15)],
            [("Display", "الشاشة", "6.67\" AMOLED, 120Hz", "6.67 إنش AMOLED بتردد 120 هرتز"),
             ("Main camera", "الكاميرا الرئيسية", "108MP", "108 ميجابكسل"),
             ("Battery", "البطارية", "5000 mAh, 33W charging", "5000 مللي أمبير، شحن 33 واط")]);

        var iphoneCase = Accessory(apple, cases, "iPhone 16 Pro Silicone Case", "غطاء سيليكون لآيفون 16 برو", "iphone-16-pro-silicone-case",
            "Soft-touch silicone with MagSafe.", "سيليكون ناعم الملمس يدعم MagSafe.",
            [("ACC-IP16P-CASE-BK", Black, "#1C1C1E", 39m, 25), ("ACC-IP16P-CASE-BL", Blue, "#5A7DB5", 39m, 18)]);

        var galaxyCase = Accessory(samsung, cases, "Galaxy S24 Clear Case", "غطاء شفاف لجالاكسي S24", "galaxy-s24-clear-case",
            "Slim transparent protection.", "حماية شفافة ونحيفة.",
            [("ACC-GS24-CLEAR", null, null, 15m, 40)]);

        var glass = Accessory(apple, protectors, "Tempered Glass for iPhone 16 Pro", "زجاج حماية لآيفون 16 برو", "iphone-16-pro-tempered-glass",
            "9H hardness, easy-install frame included.", "صلابة 9H مع إطار لسهولة التركيب.",
            [("ACC-IP16P-GLASS", null, null, 9m, 60)]);

        var charger = Accessory(anker, chargers, "Anker Nano 30W USB-C Charger", "شاحن أنكر نانو 30 واط USB-C", "anker-nano-30w",
            "Compact fast charger for phones and tablets.", "شاحن سريع صغير الحجم للهواتف والأجهزة اللوحية.",
            [("ACC-ANK-30W-WH", White, "#F2F2F2", 25m, 35), ("ACC-ANK-30W-BK", Black, "#222222", 25m, 12)]);

        var earbuds = Accessory(samsung, audio, "Galaxy Buds FE", "سماعات جالاكسي بادز FE", "galaxy-buds-fe",
            "Active noise cancellation with a comfortable fit.", "عزل نشط للضوضاء مع تصميم مريح.",
            [("ACC-BUDSFE-BK", Black, "#2B2B2B", 79m, 10), ("ACC-BUDSFE-WH", White, "#EDEDED", 79m, 2)]);

        iphoneCase.SetCompatiblePhones([iphone.Id]);
        glass.SetCompatiblePhones([iphone.Id]);
        galaxyCase.SetCompatiblePhones([galaxy.Id]);
        charger.SetCompatiblePhones([iphone.Id, galaxy.Id, redmi.Id]);
        earbuds.SetCompatiblePhones([galaxy.Id]);

        db.Products.AddRange(iphone, galaxy, redmi, iphoneCase, galaxyCase, glass, charger, earbuds);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Product Phone(
        Brand brand, Category category, string nameEn, string nameAr, string slug, string descEn, string descAr, bool featured,
        (string Sku, LocalizedText Color, string Hex, int Storage, int Ram, decimal Price, decimal? Compare, int Stock)[] variants,
        (string LabelEn, string LabelAr, string ValueEn, string ValueAr)[] specs)
    {
        var product = new Product(ProductType.Phone, brand.Id, category.Id, new(nameEn, nameAr), slug, new(descEn, descAr));
        foreach (var v in variants)
        {
            product.AddVariant(v.Sku, new VariantOptions(v.Color, v.Hex, v.Storage, v.Ram), v.Price, v.Compare, v.Stock);
        }

        product.ReplaceSpecifications(specs.Select(s => new SpecificationInput(new(s.LabelEn, s.LabelAr), new(s.ValueEn, s.ValueAr))));
        product.SetFeatured(featured);
        product.Publish();
        return product;
    }

    private static Product Accessory(
        Brand brand, Category category, string nameEn, string nameAr, string slug, string descEn, string descAr,
        (string Sku, LocalizedText? Color, string? Hex, decimal Price, int Stock)[] variants)
    {
        var product = new Product(ProductType.Accessory, brand.Id, category.Id, new(nameEn, nameAr), slug, new(descEn, descAr));
        foreach (var v in variants)
        {
            product.AddVariant(v.Sku, new VariantOptions(v.Color, v.Hex), v.Price, null, v.Stock);
        }

        product.Publish();
        return product;
    }
}
