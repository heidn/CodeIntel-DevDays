using CodeIntel.Server.Services;

namespace CodeIntel.Server.Tests;

public class PlSqlObjectParserTests
{
    private readonly PlSqlObjectParser _parser = new();

    // ── Empty / null input ─────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t  \n")]
    public void Parse_EmptyOrWhitespace_ReturnsEmpty(string input)
    {
        var refs = _parser.Parse(input);
        Assert.Empty(refs.Tables);
        Assert.Empty(refs.Routines);
        Assert.Empty(refs.Packages);
    }

    // ── DML keyword extraction ─────────────────────────────────────────────

    [Fact]
    public void Parse_SelectFromTable_CapturesTable()
    {
        var refs = _parser.Parse("SELECT * FROM employees");
        Assert.Contains("employees", refs.Tables);
    }

    [Fact]
    public void Parse_SchemaQualifiedTable_StripsSchemaPrefix()
    {
        var refs = _parser.Parse("SELECT * FROM hr.employees");
        Assert.Contains("employees", refs.Tables);
        Assert.DoesNotContain("hr", refs.Tables);
        Assert.DoesNotContain("hr.employees", refs.Tables);
    }

    [Fact]
    public void Parse_JoinTable_CapturesBoth()
    {
        var refs = _parser.Parse("SELECT * FROM orders o JOIN customers c ON c.id = o.customer_id");
        Assert.Contains("orders",    refs.Tables);
        Assert.Contains("customers", refs.Tables);
    }

    [Fact]
    public void Parse_InsertIntoSelectFrom_CapturesBoth()
    {
        var refs = _parser.Parse("INSERT INTO orders_archive SELECT * FROM orders WHERE status = 'CLOSED'");
        Assert.Contains("orders_archive", refs.Tables);
        Assert.Contains("orders",         refs.Tables);
    }

    [Fact]
    public void Parse_UpdateTable_CapturesTarget()
    {
        var refs = _parser.Parse("UPDATE orders SET status = 'SHIPPED' WHERE order_id = :id");
        Assert.Contains("orders", refs.Tables);
    }

    [Fact]
    public void Parse_DeleteFromTable_CapturesTarget()
    {
        var refs = _parser.Parse("DELETE FROM orders WHERE order_id = :id");
        Assert.Contains("orders", refs.Tables);
    }

    [Fact]
    public void Parse_DeleteWithoutFromKeyword_CapturesTarget()
    {
        var refs = _parser.Parse("DELETE orders WHERE order_id = :id");
        Assert.Contains("orders", refs.Tables);
    }

    [Fact]
    public void Parse_MergeIntoUsing_CapturesBoth()
    {
        var refs = _parser.Parse("MERGE INTO target_t USING source_t ON (target_t.id = source_t.id)");
        Assert.Contains("target_t", refs.Tables);
        Assert.Contains("source_t", refs.Tables);
    }

    // ── Routine + package extraction ───────────────────────────────────────

    [Fact]
    public void Parse_QualifiedPackageCall_CapturesPackageAndRoutine()
    {
        var refs = _parser.Parse("BEGIN orders_api.get_order_details(p_order_id => 42); END;");
        Assert.Contains("orders_api",        refs.Packages);
        Assert.Contains("get_order_details", refs.Routines);
    }

    [Fact]
    public void Parse_ExplicitExecute_CapturesRoutine()
    {
        var refs = _parser.Parse("EXECUTE update_order_stats;");
        Assert.Contains("update_order_stats", refs.Routines);
    }

    [Fact]
    public void Parse_ShortExecKeyword_CapturesRoutine()
    {
        var refs = _parser.Parse("EXEC refresh_summary");
        Assert.Contains("refresh_summary", refs.Routines);
    }

    [Fact]
    public void Parse_CallKeyword_CapturesRoutine()
    {
        var refs = _parser.Parse("CALL my_proc(1, 2);");
        Assert.Contains("my_proc", refs.Routines);
    }

    [Fact]
    public void Parse_BareFunctionCall_DoesNotCaptureRoutine()
    {
        // We intentionally don't match bare-identifier function calls — too many false positives
        // (LENGTH, NVL, TO_CHAR, ...). Only EXEC/CALL or package.proc patterns should match.
        var refs = _parser.Parse("DECLARE v_count NUMBER; BEGIN v_count := LENGTH('hello'); END;");
        Assert.Empty(refs.Routines);
        Assert.Empty(refs.Packages);
    }

    // ── Comment + string stripping ─────────────────────────────────────────

    [Fact]
    public void Parse_LineCommentWithFakeReference_IsIgnored()
    {
        var src = "-- this references FROM ghost_table only in a comment\nSELECT * FROM real_table";
        var refs = _parser.Parse(src);
        Assert.Contains("real_table",     refs.Tables);
        Assert.DoesNotContain("ghost_table", refs.Tables);
    }

    [Fact]
    public void Parse_BlockCommentWithFakeReference_IsIgnored()
    {
        var src = "/* FROM ghost_table is in a block comment */ SELECT * FROM real_table";
        var refs = _parser.Parse(src);
        Assert.Contains("real_table",     refs.Tables);
        Assert.DoesNotContain("ghost_table", refs.Tables);
    }

    [Fact]
    public void Parse_StringLiteralWithFakeKeywords_IsIgnored()
    {
        // EXECUTE IMMEDIATE's payload is inside a string literal — we should NOT pull
        // references out of dynamic SQL strings (deferred to a future enhancement).
        var src = "EXECUTE IMMEDIATE 'UPDATE fake_table SET x = 1';";
        var refs = _parser.Parse(src);
        Assert.DoesNotContain("fake_table", refs.Tables);
    }

    [Fact]
    public void Parse_DoubledQuoteInString_IsIgnored()
    {
        // PL/SQL escapes single-quote-in-string with doubled ''. Our stripper must still
        // collapse the whole literal as opaque content.
        var src = "v_msg := 'O''Brien said FROM ghost_table'; SELECT * FROM real_table;";
        var refs = _parser.Parse(src);
        Assert.Contains("real_table",     refs.Tables);
        Assert.DoesNotContain("ghost_table", refs.Tables);
    }

    // ── Stop-word filtering ────────────────────────────────────────────────

    [Fact]
    public void Parse_SelectFromDual_DoesNotCaptureDual()
    {
        var refs = _parser.Parse("SELECT SYSDATE FROM dual");
        Assert.DoesNotContain("dual", refs.Tables, StringComparer.OrdinalIgnoreCase);
    }

    // ── Dedup + sort ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_RepeatedReference_IsDedupedCaseInsensitively()
    {
        var src = "SELECT * FROM orders WHERE EXISTS (SELECT 1 FROM Orders WHERE ORDERS.id = orders.id)";
        var refs = _parser.Parse(src);
        Assert.Single(refs.Tables);
    }

    [Fact]
    public void Parse_MultipleNames_AreSortedAlphabetically()
    {
        var src = "SELECT * FROM zebras z JOIN apples a ON a.id = z.id JOIN mangoes m ON m.id = a.id";
        var refs = _parser.Parse(src);
        Assert.Equal(new[] { "apples", "mangoes", "zebras" }, refs.Tables);
    }

    // ── Realistic end-to-end ───────────────────────────────────────────────

    [Fact]
    public void Parse_PackageBodyExample_CapturesExpectedRefs()
    {
        var src = """
            CREATE OR REPLACE PACKAGE BODY orders_api AS
              PROCEDURE archive_old_orders IS
              BEGIN
                INSERT INTO orders_archive
                SELECT o.*
                FROM   orders o
                JOIN   customers c ON c.customer_id = o.customer_id
                WHERE  o.order_date < SYSDATE - 365;

                DELETE FROM orders WHERE order_date < SYSDATE - 365;

                audit_pkg.log_event('archive_complete');
              END archive_old_orders;
            END orders_api;
            """;
        var refs = _parser.Parse(src);

        Assert.Contains("orders_archive", refs.Tables);
        Assert.Contains("orders",         refs.Tables);
        Assert.Contains("customers",      refs.Tables);
        Assert.Contains("audit_pkg",      refs.Packages);
        Assert.Contains("log_event",      refs.Routines);
    }
}
