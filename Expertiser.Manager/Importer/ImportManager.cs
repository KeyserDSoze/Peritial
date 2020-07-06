using Expertiser.Database.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Expertiser
{
    public class ImportManager
    {
        private readonly Manager Manager;
        public ImportManager(Manager manager)
            => this.Manager = manager;
        private static readonly ImporterFactory Factory = new ImporterFactory();
        public async Task<int> FinalizeAsync(IFormFile file, ImporterType importerType)
        {
            IImporter importer = Factory.Create(importerType);
            //Create customer of insurance
            ImportedExperty importValue = importer.Import(file.OpenReadStream(), Manager);
            int customerId = await GetCustomerId(importValue.Customer);
            int contractorId = await GetCustomerId(importValue.Contractor);

            //Create insurance agency
            var insuranceAgency = await Manager.Context.TableInsuranceAgency.FirstOrDefaultAsync(x => x.ExternalId == importValue.InsuranceAgency.ExternalId);
            if (insuranceAgency == null)
            {
                insuranceAgency = new TableInsuranceAgency()
                {
                    InsuranceCompanyId = importValue.Insurance.InsuranceCompanyId,
                    Label = importValue.InsuranceAgency.ExternalId,
                    ExternalId = importValue.InsuranceAgency.ExternalId
                };
                Manager.Context.Add(insuranceAgency);
                await Manager.Context.SaveChangesAsync();
            }
            //Create insurance inspectorate
            var inspectorate = await Manager.Context.TableInspectorate.FirstOrDefaultAsync(x => x.ExternalId == importValue.Inspectorate.ExternalId);
            if (inspectorate == null)
            {
                inspectorate = new TableInspectorate()
                {
                    InsuranceCompanyId = importValue.Insurance.InsuranceCompanyId,
                    Label = importValue.Inspectorate.ExternalId,
                    ExternalId = importValue.Inspectorate.ExternalId
                };
                Manager.Context.Add(inspectorate);
                await Manager.Context.SaveChangesAsync();
            }
            //Create insurance
            var insurance = await Manager.Context.TableInsurance.FirstOrDefaultAsync(x => x.ExternalId == importValue.Insurance.ExternalId);
            if (insurance == null)
            {
                insurance = new TableInsurance()
                {
                    CustomerId = customerId,
                    ExternalId = importValue.Insurance.ExternalId,
                    ContractorId = contractorId,
                    InsuranceCompanyId = importValue.Insurance.InsuranceCompanyId,
                };
                Manager.Context.TableInsurance.Add(insurance);
                await Manager.Context.SaveChangesAsync();
            }
            //Create insurance adjuster
            var insuranceAdjuster = await Manager.Context.TableInsuranceAdjuster.FirstOrDefaultAsync(x => x.Name == importValue.InsuranceAdjuster.Name);
            if (insuranceAdjuster == null)
            {
                insuranceAdjuster = new TableInsuranceAdjuster()
                {
                    Name = importValue.InsuranceAdjuster.Name
                };
                Manager.Context.TableInsuranceAdjuster.Add(insuranceAdjuster);
                await Manager.Context.SaveChangesAsync();
            }
            //Create accident
            var accident = await Manager.Context.TableAccident.FirstOrDefaultAsync(x => x.ExternalId == importValue.Accident.ExternalId);
            if (accident == null)
            {
                accident = new TableAccident()
                {
                    ExternalId = importValue.Accident.ExternalId,
                    DenunciationDate = importValue.Accident.DenunciationDate,
                    AccidentDate = importValue.Accident.AccidentDate,
                    InsuranceId = insurance.Id,
                    InsuranceAdjusterId = insuranceAdjuster.Id,
                    InspectorateId = inspectorate.Id,
                    InsuranceAgencyId = insuranceAgency.Id,
                };
                await Manager.QueryManager.InsertAccidentAsync(accident);
            }
            foreach (var damaged in importValue.Damageds)
            {
                int damagedId = await GetCustomerId(damaged);
                if (!await Manager.Context.TableDamaged.AnyAsync(x => x.AccidentId == accident.Id && x.CustomerId == damagedId))
                {
                    Manager.Context.Add(new TableDamaged()
                    {
                        AccidentId = accident.Id,
                        CustomerId = damagedId
                    });
                    await Manager.Context.SaveChangesAsync();
                }
            }
            var expertise = await Manager.Context.TableExpertise.FirstOrDefaultAsync(x => x.AccidentId == accident.Id);
            if (expertise == null)
            {
                expertise = new TableExpertise()
                {
                    AccidentId = accident.Id,
                    BranchId = importValue.Expertise.BranchId,
                    Status = importValue.Expertise.Status,
                    FileId = Guid.NewGuid().ToString("N")
                };
                Manager.Context.TableExpertise.Add(expertise);
                await Manager.Context.SaveChangesAsync();
            }
            foreach (var importedFile in importValue.Files)
            {
                using (var stream = new MemoryStream(importedFile.Content))
                {
                    stream.Position = 0;
                    await Manager.FileManager.SaveFile(expertise.FileId, stream, importedFile.Name, importedFile.ContentType, $"insurancecompany/{importedFile.Name}");
                }
            }
            return accident.Id;

            async Task<int> GetCustomerId(TableCustomer tableCustomer)
            {
                var city = await Manager.Context.TableCity.FirstOrDefaultAsync(x => x.PostalCode.Contains(tableCustomer.Address.PostalCode));
                if (city == null)
                    city = await Manager.Context.TableCity.FirstOrDefaultAsync(x => x.Name == "Nessuna");
                var address = await Manager.Context.TableLocation.FirstOrDefaultAsync(x => x.CityId == city.Id && x.Address == tableCustomer.Address.Address);
                if (address == null)
                {
                    address = new TableLocation()
                    {
                        Address = tableCustomer.Address.Address,
                        CityId = city.Id,
                        PostalCode = tableCustomer.Address.PostalCode,
                    };
                    Manager.Context.TableLocation.Add(address);
                    await Manager.Context.SaveChangesAsync();
                }
                var customer = string.IsNullOrWhiteSpace(tableCustomer.FiscalCode) ?
                await Manager.Context.TableCustomer.FirstOrDefaultAsync(x => x.Name == tableCustomer.Name)
                : await Manager.Context.TableCustomer.FirstOrDefaultAsync(x => x.FiscalCode == tableCustomer.FiscalCode);
                if (customer == null)
                {
                    customer = new TableCustomer()
                    {
                        AddressId = address.Id,
                        FiscalCode = tableCustomer.FiscalCode,
                        Name = tableCustomer.Name,
                    };
                    Manager.Context.TableCustomer.Add(customer);
                    await Manager.Context.SaveChangesAsync();
                }
                return customer.Id;
            }
        }
    }
}