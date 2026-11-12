using CompanyManagement.API.Infrastructure;
using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Reports;
using CompanyManagement.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CompanyManagement.API.Controllers;

/// <summary>
/// Task review report. Open to admins, managers, department heads and team leaders (tasks.reviews.view); the data is scoped to the
/// people they manage, so they may also export it.
/// </summary>
[ApiController]
[Route("api/reports/task-reviews")]
[Authorize(Policy = Permissions.TaskReviewsView)]
public class TaskReviewReportController(IReportService reports, IExportService export) : ControllerBase
{
    /// <summary>Task approvals / rejections with ratings in the period. <c>?format=xlsx|pdf&amp;view=items</c> exports the review list instead of the per-employee summary.</summary>
    [HttpGet]
    public async Task<IActionResult> TaskReviews([FromQuery] ReportQuery q, [FromQuery] string? view, CancellationToken ct)
    {
        var data = await reports.TaskReviewsAsync(q, ct);
        if (string.IsNullOrEmpty(q.Format) || q.Format == "json") return Ok(data);
        var subtitle = $"{data.From:yyyy-MM-dd} – {data.To:yyyy-MM-dd} · Reviews {data.Reviews} · Approved {data.Approved} · Rejected {data.Rejected}"
                       + (data.AverageRating is double avg ? $" · Average rating {avg}/10" : "");
        if (view == "items")
            return ExportResults.File(this, export, q.Format!, "task-reviews", "Task Reviews", subtitle, data.Items,
            [
                new("Date", r => r.ReviewedAt), new("Task", r => r.TaskTitle), new("Project", r => r.ProjectName),
                new("Department", r => r.DepartmentName), new("Assignees", r => r.Assignees), new("Reviewer", r => r.ReviewerName),
                new("Decision", r => r.Approved ? "Approved" : "Rejected"), new("Rating", r => r.Rating), new("Notes", r => r.Notes),
            ]);
        return ExportResults.File(this, export, q.Format!, "task-review-summary", "Task Review Summary", subtitle, data.ByEmployee,
        [
            new("Code", r => r.EmployeeCode), new("Employee", r => r.EmployeeName), new("Department", r => r.DepartmentName),
            new("Reviews", r => r.Reviews), new("Approved", r => r.Approved), new("Rejected", r => r.Rejected),
            new("Approval %", r => r.ApprovalRate), new("Avg rating", r => r.AverageRating), new("Min", r => r.MinRating), new("Max", r => r.MaxRating),
        ]);
    }
}
