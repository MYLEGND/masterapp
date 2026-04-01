using System.Data;
using System.Data.Common;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;
using System.Security.Claims;
using AgentPortal.Services;

namespace AgentPortal.Controllers;

[Authorize]
[ApiController]
[Route("WorkstationNotes")]
public class WorkstationNotesController : ControllerBase
{
    private readonly MasterAppDbContext _db;
    private readonly EffectiveAgentContext _agentContext;

    public WorkstationNotesController(MasterAppDbContext db, EffectiveAgentContext agentContext)
    {
        _db = db;
        _agentContext = agentContext;
    }

    private string AgentId
    {
        get
        {
            var eff = _agentContext.EffectiveAgentOid;
            if (!string.IsNullOrWhiteSpace(eff)) return eff.Trim();

            return (User?.GetStableUserId()
                ?? User?.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? string.Empty).Trim();
        }
    }

    private sealed class NoteRow
    {
        public string AgentUserId { get; set; } = string.Empty;
        public string LeadId { get; set; } = string.Empty;
        public string LeadName { get; set; } = string.Empty;
        public string NoteDate { get; set; } = string.Empty;
        public string WentWell { get; set; } = string.Empty;
        public string CouldBetter { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    [HttpGet("Dates")]
    public async Task<IActionResult> Dates([FromQuery] string? leadId)
    {
        if (string.IsNullOrWhiteSpace(AgentId)) return Forbid();
        await EnsureTableAsync();

        var normalizedLeadId = NormalizeLeadId(leadId);

        var sql = @"
            SELECT LeadId, LeadName, NoteDate, UpdatedUtc
            FROM AgentLeadSelfNotes
                        WHERE AgentUserId = @agent
                            AND (WentWell <> '' OR CouldBetter <> '')";

        if (!string.IsNullOrWhiteSpace(normalizedLeadId))
            sql += " AND LeadId = @leadId";

        sql += " ORDER BY UpdatedUtc DESC";

        var rows = await QueryRowsAsync(sql,
            cmd =>
            {
                var p = cmd.CreateParameter();
                p.ParameterName = "@agent";
                p.Value = AgentId;
                cmd.Parameters.Add(p);

                if (!string.IsNullOrWhiteSpace(normalizedLeadId))
                {
                    var pLead = cmd.CreateParameter();
                    pLead.ParameterName = "@leadId";
                    pLead.Value = normalizedLeadId;
                    cmd.Parameters.Add(pLead);
                }
            });

        var payload = rows
            .Select(r => new
            {
                leadId = r.LeadId,
                leadName = r.LeadName,
                noteDate = r.NoteDate,
                displayDate = DisplayDate(r.NoteDate),
                updatedUtc = r.UpdatedUtc,
                label = $"{(string.IsNullOrWhiteSpace(r.LeadName) ? "Lead" : r.LeadName)} — {DisplayDate(r.NoteDate)}"
            })
            .ToList();

        return Ok(payload);
    }

    [HttpGet("Entry")]
    public async Task<IActionResult> Entry([FromQuery] string? leadId, [FromQuery] string? date)
    {
        if (string.IsNullOrWhiteSpace(AgentId)) return Forbid();

        var normalizedDate = NormalizeDate(date);
        if (normalizedDate == null) return BadRequest("Invalid date.");
        var normalizedLeadId = NormalizeLeadId(leadId);
        if (normalizedLeadId == null) return BadRequest("LeadId is required.");

        await EnsureTableAsync();

        var row = await QuerySingleAsync(@"
            SELECT AgentUserId, LeadId, LeadName, NoteDate, WentWell, CouldBetter, CreatedUtc, UpdatedUtc
            FROM AgentLeadSelfNotes
            WHERE AgentUserId = @agent AND LeadId = @leadId AND NoteDate = @date",
            cmd =>
            {
                var p1 = cmd.CreateParameter();
                p1.ParameterName = "@agent";
                p1.Value = AgentId;
                cmd.Parameters.Add(p1);

                var p2 = cmd.CreateParameter();
                p2.ParameterName = "@leadId";
                p2.Value = normalizedLeadId;
                cmd.Parameters.Add(p2);

                var p3 = cmd.CreateParameter();
                p3.ParameterName = "@date";
                p3.Value = normalizedDate;
                cmd.Parameters.Add(p3);
            });

        if (row == null)
        {
            return Ok(new
            {
                leadId = normalizedLeadId,
                leadName = string.Empty,
                date = normalizedDate,
                displayDate = DisplayDate(normalizedDate),
                wentWell = string.Empty,
                couldBetter = string.Empty,
                updatedUtc = (DateTime?)null
            });
        }

        return Ok(new
        {
            leadId = row.LeadId,
            leadName = row.LeadName,
            date = row.NoteDate,
            displayDate = DisplayDate(row.NoteDate),
            wentWell = row.WentWell,
            couldBetter = row.CouldBetter,
            updatedUtc = row.UpdatedUtc
        });
    }

    public sealed class SaveNoteRequest
    {
        public string? LeadId { get; set; }
        public string? LeadName { get; set; }
        public string? Date { get; set; }
        public string? WentWell { get; set; }
        public string? CouldBetter { get; set; }
    }

    [HttpPost("Entry")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveEntry([FromBody] SaveNoteRequest request)
    {
        if (string.IsNullOrWhiteSpace(AgentId)) return Forbid();

        var normalizedLeadId = NormalizeLeadId(request.LeadId);
        if (normalizedLeadId == null) return BadRequest("LeadId is required.");

        var normalizedDate = NormalizeDate(request.Date);
        if (normalizedDate == null) return BadRequest("Invalid date.");

        var leadName = (request.LeadName ?? string.Empty).Trim();
        var wentWell = (request.WentWell ?? string.Empty).Trim();
        var couldBetter = (request.CouldBetter ?? string.Empty).Trim();

        await EnsureTableAsync();

        var now = DateTime.UtcNow;

        // Do not keep empty dated entries; remove any existing row for that lead/date.
        if (string.IsNullOrWhiteSpace(wentWell) && string.IsNullOrWhiteSpace(couldBetter))
        {
            if (_db.Database.IsSqlite())
            {
                await _db.Database.ExecuteSqlRawAsync(@"
                    DELETE FROM AgentLeadSelfNotes
                    WHERE AgentUserId = {0} AND LeadId = {1} AND NoteDate = {2}",
                    AgentId, normalizedLeadId, normalizedDate);
            }
            else
            {
                await _db.Database.ExecuteSqlRawAsync(@"
                    DELETE FROM AgentLeadSelfNotes
                    WHERE AgentUserId = {0} AND LeadId = {1} AND NoteDate = {2}",
                    AgentId, normalizedLeadId, normalizedDate);
            }

            return Ok(new
            {
                leadId = normalizedLeadId,
                leadName,
                date = normalizedDate,
                displayDate = DisplayDate(normalizedDate),
                wentWell = string.Empty,
                couldBetter = string.Empty,
                deleted = true,
                updatedUtc = now
            });
        }

        if (_db.Database.IsSqlite())
        {
            await _db.Database.ExecuteSqlRawAsync(@"
                INSERT INTO AgentLeadSelfNotes (AgentUserId, LeadId, LeadName, NoteDate, WentWell, CouldBetter, CreatedUtc, UpdatedUtc)
                VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7})
                ON CONFLICT(AgentUserId, LeadId, NoteDate)
                DO UPDATE SET LeadName = excluded.LeadName,
                              WentWell = excluded.WentWell,
                              CouldBetter = excluded.CouldBetter,
                              UpdatedUtc = excluded.UpdatedUtc",
                AgentId, normalizedLeadId, leadName, normalizedDate, wentWell, couldBetter, now, now);
        }
        else
        {
            await _db.Database.ExecuteSqlRawAsync(@"
                IF EXISTS (SELECT 1 FROM AgentLeadSelfNotes WHERE AgentUserId = {0} AND LeadId = {1} AND NoteDate = {2})
                BEGIN
                    UPDATE AgentLeadSelfNotes
                    SET LeadName = {3}, WentWell = {4}, CouldBetter = {5}, UpdatedUtc = {6}
                    WHERE AgentUserId = {0} AND LeadId = {1} AND NoteDate = {2}
                END
                ELSE
                BEGIN
                    INSERT INTO AgentLeadSelfNotes (AgentUserId, LeadId, LeadName, NoteDate, WentWell, CouldBetter, CreatedUtc, UpdatedUtc)
                    VALUES ({0}, {1}, {3}, {2}, {4}, {5}, {6}, {6})
                END",
                AgentId, normalizedLeadId, normalizedDate, leadName, wentWell, couldBetter, now);
        }

        return Ok(new
        {
            leadId = normalizedLeadId,
            leadName,
            date = normalizedDate,
            displayDate = DisplayDate(normalizedDate),
            wentWell,
            couldBetter,
            updatedUtc = now
        });
    }

    private static string? NormalizeLeadId(string? leadId)
    {
        var normalized = (leadId ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string DisplayDate(string? isoDate)
    {
        return DateOnly.TryParse(isoDate, out var parsed)
            ? parsed.ToString("MM-dd-yyyy")
            : "00-00-0000";
    }

    private static string? NormalizeDate(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        return DateOnly.TryParse(date, out var parsed)
            ? parsed.ToString("yyyy-MM-dd")
            : null;
    }

    private async Task EnsureTableAsync()
    {
        if (_db.Database.IsSqlite())
        {
            await _db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS AgentLeadSelfNotes (
                    AgentUserId TEXT NOT NULL,
                    LeadId TEXT NOT NULL,
                    LeadName TEXT NOT NULL,
                    NoteDate TEXT NOT NULL,
                    WentWell TEXT NOT NULL,
                    CouldBetter TEXT NOT NULL,
                    CreatedUtc TEXT NOT NULL,
                    UpdatedUtc TEXT NOT NULL,
                    PRIMARY KEY (AgentUserId, LeadId, NoteDate)
                )");
            return;
        }

        await _db.Database.ExecuteSqlRawAsync(@"
            IF OBJECT_ID(N'AgentLeadSelfNotes', N'U') IS NULL
            BEGIN
                CREATE TABLE AgentLeadSelfNotes (
                    AgentUserId nvarchar(450) NOT NULL,
                    LeadId nvarchar(128) NOT NULL,
                    LeadName nvarchar(240) NOT NULL,
                    NoteDate nvarchar(10) NOT NULL,
                    WentWell nvarchar(max) NOT NULL,
                    CouldBetter nvarchar(max) NOT NULL,
                    CreatedUtc datetime2 NOT NULL,
                    UpdatedUtc datetime2 NOT NULL,
                    CONSTRAINT PK_AgentLeadSelfNotes PRIMARY KEY (AgentUserId, LeadId, NoteDate)
                )
            END");
    }

    private async Task<List<NoteRow>> QueryRowsAsync(string sql, Action<DbCommand>? configure)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        configure?.Invoke(cmd);

        var list = new List<NoteRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new NoteRow
            {
                LeadId = reader["LeadId"]?.ToString() ?? string.Empty,
                LeadName = reader["LeadName"]?.ToString() ?? string.Empty,
                NoteDate = reader["NoteDate"]?.ToString() ?? "",
                UpdatedUtc = TryParseDateTime(reader["UpdatedUtc"]) ?? DateTime.UtcNow
            });
        }
        return list;
    }

    private async Task<NoteRow?> QuerySingleAsync(string sql, Action<DbCommand>? configure)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        configure?.Invoke(cmd);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new NoteRow
        {
            AgentUserId = reader["AgentUserId"]?.ToString() ?? "",
            LeadId = reader["LeadId"]?.ToString() ?? string.Empty,
            LeadName = reader["LeadName"]?.ToString() ?? string.Empty,
            NoteDate = reader["NoteDate"]?.ToString() ?? "",
            WentWell = reader["WentWell"]?.ToString() ?? "",
            CouldBetter = reader["CouldBetter"]?.ToString() ?? "",
            CreatedUtc = TryParseDateTime(reader["CreatedUtc"]) ?? DateTime.UtcNow,
            UpdatedUtc = TryParseDateTime(reader["UpdatedUtc"]) ?? DateTime.UtcNow
        };
    }

    private static DateTime? TryParseDateTime(object? value)
    {
        if (value is DateTime dt) return dt;
        if (value == null) return null;
        return DateTime.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }
}
