using System;
using System.Collections.Generic;

namespace Expertiser.Portal.Models
{
    public class KpiViewModel
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public List<ContributorAssignmentsKpi> ContributorAssignments { get; set; }
        public List<ContributorClosingTimeKpi> ContributorClosingTimes { get; set; }
        public List<ContributorLiquidatedByBranchKpi> ContributorLiquidatedByBranch { get; set; }
    }

    public class ContributorAssignmentsKpi
    {
        public string ContributorId { get; set; }
        public string ContributorName { get; set; }
        public int TotalAssignments { get; set; }
    }

    public class ContributorClosingTimeKpi
    {
        public string ContributorId { get; set; }
        public string ContributorName { get; set; }
        public int ClosedCases { get; set; }
        public double AverageClosingDays { get; set; }
    }

    public class ContributorLiquidatedByBranchKpi
    {
        public string ContributorId { get; set; }
        public string ContributorName { get; set; }
        public int BranchId { get; set; }
        public string BranchName { get; set; }
        public int TotalCases { get; set; }
        public decimal TotalLiquidated { get; set; }
    }
}
