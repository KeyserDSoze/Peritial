using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_Expertise")]
    public partial class TableExpertise
    {
        public TableExpertise()
        {
            TableMessages = new HashSet<TableMessages>();
        }

        [Key]
        public int Id { get; set; }
        public int MemberId { get; set; }
        public int? ContributorId { get; set; }
        public int CounterPartId { get; set; }
        public int Status { get; set; }
        public int InsuranceAgencyId { get; set; }
        public int InspectorateId { get; set; }
        public int Branch { get; set; }
        public int BranchType { get; set; }
        public int PlaceId { get; set; }
        public int InsuranceId { get; set; }
        [Required]
        [StringLength(128)]
        public string ExternalId { get; set; }
        [Column(TypeName = "datetime")]
        public DateTime AccidentDate { get; set; }
        [Column(TypeName = "datetime")]
        public DateTime? ComplaintDate { get; set; }
        [Column(TypeName = "datetime")]
        public DateTime? AssignmentDate { get; set; }
        [Column(TypeName = "datetime")]
        public DateTime? InspectionDate { get; set; }
        [Required]
        [StringLength(128)]
        public string AccidentId { get; set; }

        [ForeignKey(nameof(ContributorId))]
        [InverseProperty(nameof(TableContributor.TableExpertise))]
        public virtual TableContributor Contributor { get; set; }
        [ForeignKey(nameof(CounterPartId))]
        [InverseProperty(nameof(TableCustomer.TableExpertise))]
        public virtual TableCustomer CounterPart { get; set; }
        [ForeignKey(nameof(InspectorateId))]
        [InverseProperty(nameof(TableInspectorate.TableExpertise))]
        public virtual TableInspectorate Inspectorate { get; set; }
        [ForeignKey(nameof(InsuranceId))]
        [InverseProperty(nameof(TableInsurance.TableExpertise))]
        public virtual TableInsurance Insurance { get; set; }
        [ForeignKey(nameof(InsuranceAgencyId))]
        [InverseProperty(nameof(TableInsuranceAgency.TableExpertise))]
        public virtual TableInsuranceAgency InsuranceAgency { get; set; }
        [ForeignKey(nameof(MemberId))]
        [InverseProperty(nameof(TableMember.TableExpertise))]
        public virtual TableMember Member { get; set; }
        [InverseProperty("Expertise")]
        public virtual ICollection<TableMessages> TableMessages { get; set; }
    }
}
