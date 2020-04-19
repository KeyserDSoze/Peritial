using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_Accident")]
    public partial class TableAccident
    {
        public TableAccident()
        {
            TableExpertise = new HashSet<TableExpertise>();
        }

        [Key]
        public int Id { get; set; }
        public int PlaceId { get; set; }
        public int InsuranceId { get; set; }
        public int InsuranceAgencyId { get; set; }
        public int InspectorateId { get; set; }
        [Required]
        [StringLength(100)]
        public string ExternalId { get; set; }
        [Column(TypeName = "datetime")]
        public DateTime AccidentDate { get; set; }
        [Column(TypeName = "datetime")]
        public DateTime? DenunciationDate { get; set; }

        [ForeignKey(nameof(InspectorateId))]
        [InverseProperty(nameof(TableInspectorate.TableAccident))]
        public virtual TableInspectorate Inspectorate { get; set; }
        [ForeignKey(nameof(InsuranceId))]
        [InverseProperty(nameof(TableInsurance.TableAccident))]
        public virtual TableInsurance Insurance { get; set; }
        [ForeignKey(nameof(InsuranceAgencyId))]
        [InverseProperty(nameof(TableInsuranceAgency.TableAccident))]
        public virtual TableInsuranceAgency InsuranceAgency { get; set; }
        [ForeignKey(nameof(PlaceId))]
        [InverseProperty(nameof(TableLocation.TableAccident))]
        public virtual TableLocation Place { get; set; }
        [InverseProperty("Accident")]
        public virtual ICollection<TableExpertise> TableExpertise { get; set; }
    }
}
