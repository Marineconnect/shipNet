using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public static class PricingPlanExcelTemplate
{
    private static readonly string[] Headers =
    [
        "Tên gói",
        "Mã gói",
        "Dung lượng (GB)",
        "Giá Cost KVH",
        "Đơn giá đại lý",
        "Đơn giá bán ra",
        "Giá Cost Overcharge KVH",
        "Giá mua thêm đại lý",
        "Giá mua thêm bán ra",
        "Trạng thái"
    ];

    private static readonly string[] TenantPricingHeaders =
    [
        "Tenant",
        "Mã gói",
        "Tên gói",
        "Đơn giá đại lý",
        "Đơn giá bán ra",
        "Giá mua thêm đại lý",
        "Giá mua thêm bán ra"
    ];

    public static byte[] CreateTemplate(IReadOnlyList<PricingPlanFormViewModel> plans)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                </Types>
                """);
            WriteEntry(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
            WriteEntry(archive, "xl/workbook.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets>
                    <sheet name="Pricing" sheetId="1" r:id="rId1"/>
                  </sheets>
                </workbook>
                """);
            WriteEntry(archive, "xl/styles.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>
                  <fills count="1"><fill><patternFill patternType="none"/></fill></fills>
                  <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
                  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                  <cellXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/></cellXfs>
                </styleSheet>
                """);
            WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(plans));
        }

        return stream.ToArray();
    }

    public static PricingPlanImportResult Parse(Stream stream, string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase)
            ? ParseCsv(stream)
            : ParseXlsx(stream);
    }

    public static byte[] CreateTenantPricingExport(IReadOnlyList<TenantPricingListItemViewModel> prices, Func<int, string> protectTenantId)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                </Types>
                """);
            WriteEntry(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
            WriteEntry(archive, "xl/workbook.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets>
                    <sheet name="TenantPricing" sheetId="1" r:id="rId1"/>
                  </sheets>
                </workbook>
                """);
            WriteEntry(archive, "xl/styles.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>
                  <fills count="1"><fill><patternFill patternType="none"/></fill></fills>
                  <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
                  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                  <cellXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/></cellXfs>
                </styleSheet>
                """);
            WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildTenantPricingWorksheetXml(prices, protectTenantId));
        }

        return stream.ToArray();
    }

    public static TenantPricingImportResult ParseTenantPricing(Stream stream, string fileName, Func<string, int?> unprotectTenantId)
    {
        var extension = Path.GetExtension(fileName);
        return string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase)
            ? ParseTenantPricingCsv(stream, unprotectTenantId)
            : ParseTenantPricingXlsx(stream, unprotectTenantId);
    }

    private static string BuildWorksheetXml(IReadOnlyList<PricingPlanFormViewModel> plans)
    {
        var rows = new List<string>
        {
            BuildRow(1, Headers)
        };

        for (var index = 0; index < plans.Count; index++)
        {
            var plan = plans[index];
            rows.Add(BuildRow(index + 2, [
                plan.PlanName,
                plan.PlanCode,
                FormatDecimal(plan.BaseData),
                FormatDecimal(plan.CostPrice),
                FormatDecimal(plan.ResellerPrice),
                FormatDecimal(plan.FinalPrice),
                FormatDecimal(plan.CostOverChargePrice),
                FormatDecimal(plan.ResellerOverChargePrice),
                FormatDecimal(plan.FinalOverChargePrice),
                NormalizeStatus(plan.Status)
            ]));
        }

        var lastRow = Math.Max(1, plans.Count + 1);
        return $$"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <dimension ref="A1:J{{lastRow}}"/>
              <sheetViews><sheetView workbookViewId="0"/></sheetViews>
              <sheetFormatPr defaultRowHeight="15"/>
              <cols>
                <col min="1" max="2" width="24" customWidth="1"/>
                <col min="3" max="10" width="22" customWidth="1"/>
              </cols>
              <sheetData>
                {{string.Join(Environment.NewLine, rows)}}
              </sheetData>
            </worksheet>
            """;
    }

    private static string BuildTenantPricingWorksheetXml(IReadOnlyList<TenantPricingListItemViewModel> prices, Func<int, string> protectTenantId)
    {
        var rows = new List<string> { BuildRow(1, TenantPricingHeaders) };

        for (var index = 0; index < prices.Count; index++)
        {
            var price = prices[index];
            rows.Add(BuildRow(index + 2, [
                price.TenantName,
                price.PlanCode,
                price.PlanName,
                FormatDecimal(price.ResellerPrice),
                FormatDecimal(price.FinalPrice),
                FormatDecimal(price.ResellerOverChargePrice),
                FormatDecimal(price.FinalOverChargePrice)
            ]));
        }

        var lastRow = Math.Max(1, prices.Count + 1);
        return $$"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <dimension ref="A1:G{{lastRow}}"/>
              <sheetViews><sheetView workbookViewId="0"/></sheetViews>
              <sheetFormatPr defaultRowHeight="15"/>
              <cols>
                <col min="1" max="3" width="24" customWidth="1"/>
                <col min="4" max="7" width="22" customWidth="1"/>
              </cols>
              <sheetData>
                {{string.Join(Environment.NewLine, rows)}}
              </sheetData>
            </worksheet>
            """;
    }

    private static PricingPlanImportResult ParseXlsx(Stream stream)
    {
        var result = new PricingPlanImportResult();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var sheetEntry = archive.GetEntry("xl/worksheets/sheet1.xml");
        if (sheetEntry is null)
        {
            result.Errors.Add("File Excel khÃ´ng cÃ³ sheet dá»¯ liá»‡u há»£p lá»‡.");
            return result;
        }

        var sharedStrings = ReadSharedStrings(archive);
        using var sheetStream = sheetEntry.Open();
        var document = XDocument.Load(sheetStream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = document.Descendants(ns + "row").Skip(1);
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsedPlans = new List<PricingPlanFormViewModel>();

        foreach (var row in rows)
        {
            var rowIndex = (int?)row.Attribute("r") ?? 0;
            var values = ReadRow(row, sharedStrings, ns, Headers.Length);
            if (values.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            AddParsedPlan(values, rowIndex, seenCodes, parsedPlans, result);
        }

        result.SkippedCount = result.Errors.Count;
        if (result.Errors.Count == 0)
        {
            result.CreatedCount = parsedPlans.Count;
        }

        result.Plans = parsedPlans;
        return result;
    }

    private static PricingPlanImportResult ParseCsv(Stream stream)
    {
        var result = new PricingPlanImportResult();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        _ = reader.ReadLine();

        var rowIndex = 1;
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsedPlans = new List<PricingPlanFormViewModel>();
        while (!reader.EndOfStream)
        {
            rowIndex++;
            var values = SplitCsvLine(reader.ReadLine() ?? string.Empty);
            if (values.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            AddParsedPlan(values, rowIndex, seenCodes, parsedPlans, result);
        }

        result.SkippedCount = result.Errors.Count;
        if (result.Errors.Count == 0)
        {
            result.CreatedCount = parsedPlans.Count;
        }

        result.Plans = parsedPlans;
        return result;
    }

    private static void AddParsedPlan(
        IReadOnlyList<string> values,
        int rowIndex,
        HashSet<string> seenCodes,
        List<PricingPlanFormViewModel> parsedPlans,
        PricingPlanImportResult result)
    {
        var planName = Get(values, 0).Trim();
        var planCode = Get(values, 1).Trim();
        var isLegacyRow = IsLegacyPricingRow(values);
        var costPriceIndex = isLegacyRow ? -1 : 3;
        var resellerPriceIndex = isLegacyRow ? 3 : 4;
        var finalPriceIndex = isLegacyRow ? 4 : 5;
        var costOverChargePriceIndex = isLegacyRow ? -1 : 6;
        var resellerOverChargePriceIndex = isLegacyRow ? 5 : 7;
        var finalOverChargePriceIndex = isLegacyRow ? 6 : 8;
        var status = NormalizeStatus(Get(values, isLegacyRow ? 7 : 9));

        if (string.IsNullOrWhiteSpace(planName))
        {
            result.Errors.Add($"DÃ²ng {rowIndex}: tÃªn gÃ³i khÃ´ng Ä‘Æ°á»£c Ä‘á»ƒ trá»‘ng.");
            return;
        }

        if (string.IsNullOrWhiteSpace(planCode))
        {
            result.Errors.Add($"DÃ²ng {rowIndex}: mÃ£ gÃ³i khÃ´ng Ä‘Æ°á»£c Ä‘á»ƒ trá»‘ng.");
            return;
        }

        if (!Regex.IsMatch(planCode, @"^[A-Za-z0-9._-]+$"))
        {
            result.Errors.Add($"DÃ²ng {rowIndex}: mÃ£ gÃ³i chá»‰ gá»“m chá»¯, sá»‘, dáº¥u cháº¥m, gáº¡ch ngang hoáº·c gáº¡ch dÆ°á»›i.");
            return;
        }

        if (!seenCodes.Add(planCode))
        {
            result.Errors.Add($"DÃ²ng {rowIndex}: mÃ£ gÃ³i '{planCode}' bá»‹ trÃ¹ng trong file import.");
            return;
        }

        if (!TryParseDecimal(Get(values, 2), out var baseData) ||
            !TryParseOptionalDecimal(Get(values, costPriceIndex), out var costPrice) ||
            !TryParseDecimal(Get(values, resellerPriceIndex), out var resellerPrice) ||
            !TryParseDecimal(Get(values, finalPriceIndex), out var finalPrice) ||
            !TryParseOptionalDecimal(Get(values, costOverChargePriceIndex), out var costOverChargePrice) ||
            !TryParseDecimal(Get(values, resellerOverChargePriceIndex), out var resellerOverChargePrice) ||
            !TryParseDecimal(Get(values, finalOverChargePriceIndex), out var finalOverChargePrice))
        {
            result.Errors.Add($"DÃ²ng {rowIndex}: cÃ¡c cá»™t dung lÆ°á»£ng vÃ  Ä‘Æ¡n giÃ¡ pháº£i lÃ  sá»‘ há»£p lá»‡.");
            return;
        }

        parsedPlans.Add(new PricingPlanFormViewModel
        {
            PlanName = planName,
            PlanCode = planCode,
            BaseData = baseData,
            CostPrice = costPrice,
            ResellerPrice = resellerPrice,
            FinalPrice = finalPrice,
            CostOverChargePrice = costOverChargePrice,
            ResellerOverChargePrice = resellerOverChargePrice,
            FinalOverChargePrice = finalOverChargePrice,
            Status = status
        });
    }

    private static TenantPricingImportResult ParseTenantPricingXlsx(Stream stream, Func<string, int?> unprotectTenantId)
    {
        var result = new TenantPricingImportResult();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var sheetEntry = archive.GetEntry("xl/worksheets/sheet1.xml");
        if (sheetEntry is null)
        {
            result.Errors.Add("File Excel khong co sheet du lieu hop le.");
            return result;
        }

        var sharedStrings = ReadSharedStrings(archive);
        using var sheetStream = sheetEntry.Open();
        var document = XDocument.Load(sheetStream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = document.Descendants(ns + "row").Skip(1);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsedRows = new List<TenantPricingImportRow>();

        foreach (var row in rows)
        {
            var rowIndex = (int?)row.Attribute("r") ?? 0;
            var values = ReadRow(row, sharedStrings, ns, TenantPricingHeaders.Length);
            if (values.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            AddParsedTenantPrice(values, rowIndex, seenKeys, parsedRows, result, unprotectTenantId);
        }

        result.Prices = parsedRows;
        result.SkippedCount = result.Errors.Count;
        return result;
    }

    private static TenantPricingImportResult ParseTenantPricingCsv(Stream stream, Func<string, int?> unprotectTenantId)
    {
        var result = new TenantPricingImportResult();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        _ = reader.ReadLine();

        var rowIndex = 1;
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsedRows = new List<TenantPricingImportRow>();
        while (!reader.EndOfStream)
        {
            rowIndex++;
            var values = SplitCsvLine(reader.ReadLine() ?? string.Empty);
            if (values.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            AddParsedTenantPrice(values, rowIndex, seenKeys, parsedRows, result, unprotectTenantId);
        }

        result.Prices = parsedRows;
        result.SkippedCount = result.Errors.Count;
        return result;
    }

    private static void AddParsedTenantPrice(
        IReadOnlyList<string> values,
        int rowIndex,
        HashSet<string> seenKeys,
        List<TenantPricingImportRow> parsedRows,
        TenantPricingImportResult result,
        Func<string, int?> unprotectTenantId)
    {
        var tenantName = Get(values, 0).Trim();
        if (string.IsNullOrWhiteSpace(tenantName))
        {
            result.Errors.Add($"Dong {rowIndex}: ten tenant khong duoc de trong.");
            return;
        }

        var planCode = Get(values, 1).Trim();
        if (string.IsNullOrWhiteSpace(planCode))
        {
            result.Errors.Add($"Dong {rowIndex}: ma goi khong duoc de trong.");
            return;
        }

        var planName = Get(values, 2).Trim();
        if (string.IsNullOrWhiteSpace(planName))
        {
            result.Errors.Add($"Dong {rowIndex}: ten goi khong duoc de trong.");
            return;
        }

        var key = $"{tenantName}:{planCode}";
        if (!seenKeys.Add(key))
        {
            result.Errors.Add($"Dong {rowIndex}: tenant '{tenantName}' va ma goi '{planCode}' bi trung trong file import.");
            return;
        }

        if (!TryParseDecimal(Get(values, 3), out var resellerPrice) ||
            !TryParseDecimal(Get(values, 4), out var finalPrice) ||
            !TryParseDecimal(Get(values, 5), out var resellerOverChargePrice) ||
            !TryParseDecimal(Get(values, 6), out var finalOverChargePrice))
        {
            result.Errors.Add($"Dong {rowIndex}: cac cot don gia phai la so hop le.");
            return;
        }

        parsedRows.Add(new TenantPricingImportRow
        {
            TenantName = tenantName,
            PlanCode = planCode,
            PlanName = planName,
            ResellerPrice = resellerPrice,
            FinalPrice = finalPrice,
            ResellerOverChargePrice = resellerOverChargePrice,
            FinalOverChargePrice = finalOverChargePrice
        });
    }
    private static Dictionary<int, string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return [];
        }

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return document.Descendants(ns + "si")
            .Select((item, index) => new { index, value = string.Concat(item.Descendants(ns + "t").Select(text => text.Value)) })
            .ToDictionary(item => item.index, item => item.value);
    }

    private static List<string> ReadRow(XElement row, Dictionary<int, string> sharedStrings, XNamespace ns, int columnCount)
    {
        var values = Enumerable.Repeat(string.Empty, columnCount).ToList();

        foreach (var cell in row.Elements(ns + "c"))
        {
            var cellRef = cell.Attribute("r")?.Value;
            var columnIndex = GetColumnIndex(cellRef);
            if (columnIndex < 0 || columnIndex >= values.Count)
            {
                continue;
            }

            values[columnIndex] = ReadCellValue(cell, sharedStrings, ns);
        }

        return values;
    }

    private static string ReadCellValue(XElement cell, Dictionary<int, string> sharedStrings, XNamespace ns)
    {
        var type = cell.Attribute("t")?.Value;
        if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(cell.Descendants(ns + "t").Select(text => text.Value));
        }

        var raw = cell.Element(ns + "v")?.Value ?? string.Empty;
        if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase) && int.TryParse(raw, out var sharedIndex))
        {
            return sharedStrings.TryGetValue(sharedIndex, out var value) ? value : string.Empty;
        }

        return raw;
    }

    private static int GetColumnIndex(string? cellRef)
    {
        if (string.IsNullOrWhiteSpace(cellRef))
        {
            return -1;
        }

        var index = 0;
        foreach (var ch in cellRef.TakeWhile(char.IsLetter))
        {
            index = (index * 26) + char.ToUpperInvariant(ch) - 'A' + 1;
        }

        return index - 1;
    }

    private static string BuildRow(int rowIndex, IReadOnlyList<string> values)
    {
        var cells = values
            .Select((value, index) => BuildInlineStringCell(rowIndex, index + 1, value));
        return $"<row r=\"{rowIndex}\">{string.Join(string.Empty, cells)}</row>";
    }

    private static string BuildInlineStringCell(int rowIndex, int columnIndex, string value)
    {
        return $"<c r=\"{GetColumnName(columnIndex)}{rowIndex}\" t=\"inlineStr\"><is><t>{System.Security.SecurityElement.Escape(value)}</t></is></c>";
    }

    private static string GetColumnName(int columnIndex)
    {
        var name = string.Empty;
        while (columnIndex > 0)
        {
            var modulo = (columnIndex - 1) % 26;
            name = Convert.ToChar('A' + modulo) + name;
            columnIndex = (columnIndex - modulo) / 26;
        }

        return name;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content.Trim());
    }

    private static bool TryParseDecimal(string value, out decimal parsed)
    {
        value = value.Trim().Replace("$", string.Empty).Replace(",", string.Empty);
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed) && parsed >= 0;
    }

    private static bool TryParseOptionalDecimal(string value, out decimal parsed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = 0;
            return true;
        }

        return TryParseDecimal(value, out parsed);
    }

    private static bool IsLegacyPricingRow(IReadOnlyList<string> values)
    {
        return values.Count <= 8 || (string.IsNullOrWhiteSpace(Get(values, 9)) && IsStatusValue(Get(values, 7)));
    }

    private static bool IsStatusValue(string value)
    {
        value = value.Trim();
        return string.Equals(value, "active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "inactive", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeStatus(string value)
    {
        value = value.Trim();
        return string.Equals(value, "inactive", StringComparison.OrdinalIgnoreCase)
            ? "inactive"
            : "active";
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string Get(IReadOnlyList<string> values, int index)
    {
        return index >= 0 && index < values.Count ? values[index] ?? string.Empty : string.Empty;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var values = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    builder.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                values.Add(builder.ToString());
                builder.Clear();
            }
            else
            {
                builder.Append(ch);
            }
        }

        values.Add(builder.ToString());
        return values;
    }
}

