using HRMS.Application.Features.Employees.Queries.ListEmployees;
using Microsoft.AspNetCore.Mvc;

namespace HRMS.API.Contracts
{
    /// <summary>
    /// Query string of GET /api/employees. Lives in the API layer (the Application layer stays free of ASP.NET attributes) so that
    /// Swagger documents the parameters in the same camelCase as every request and response body.
    /// </summary>
    public sealed class ListEmployeesRequest
    {
        /// <summary>1-based page number (default 1).</summary>
        [FromQuery(Name = "page")]
        public int Page { get; set; } = 1;

        /// <summary>Page size, 1 to 100 (default 20).</summary>
        [FromQuery(Name = "pageSize")]
        public int PageSize { get; set; } = ListEmployeesQuery.DefaultPageSize;

        /// <summary>Case-insensitive "contains" over name, employee code, designation and email (max 100 characters).</summary>
        [FromQuery(Name = "search")]
        public string? Search { get; set; }

        /// <summary>active | inactive | probation | notice_period | contract.</summary>
        [FromQuery(Name = "status")]
        public string? Status { get; set; }

        [FromQuery(Name = "departmentId")]
        public Guid? DepartmentId { get; set; }

        /// <summary>empCode (default) | name | designation | joiningDate | status | createdAt.</summary>
        [FromQuery(Name = "sortBy")]
        public string SortBy { get; set; } = "empCode";

        /// <summary>asc (default) | desc.</summary>
        [FromQuery(Name = "sortDirection")]
        public string SortDirection { get; set; } = "asc";

        public ListEmployeesQuery ToQuery() => new()
        {
            Page = Page,
            PageSize = PageSize,
            Search = Search,
            Status = Status,
            DepartmentId = DepartmentId,
            SortBy = SortBy,
            SortDirection = SortDirection
        };
    }
}
