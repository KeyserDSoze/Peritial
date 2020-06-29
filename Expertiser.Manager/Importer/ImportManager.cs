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
            ImportedExperty importValue = importer.Import(file.OpenReadStream());
            var accident = await Manager.Context.TableAccident.FirstOrDefaultAsync(x => x.ExternalId == importValue.Accident.ExternalId);
            if (accident == null)
            {
                var city = await Manager.Context.TableCity.FirstOrDefaultAsync(x => x.PostalCode.Contains(importValue.Location.PostalCode));
                var address = await Manager.Context.TableLocation.FirstOrDefaultAsync(x => x.CityId == city.Id && x.Address == importValue.Location.Address);
                if (address == null)
                {
                    address = new TableLocation()
                    {
                        Address = importValue.Location.Address,
                        CityId = city.Id,
                        PostalCode = importValue.Location.PostalCode,
                    };
                    Manager.Context.TableLocation.Add(address);
                    await Manager.Context.SaveChangesAsync();
                }
                var customer = string.IsNullOrWhiteSpace(importValue.Customer.FiscalCode) ?
                    await Manager.Context.TableCustomer.FirstOrDefaultAsync(x => x.Name == importValue.Customer.Name)
                    : await Manager.Context.TableCustomer.FirstOrDefaultAsync(x => x.FiscalCode == importValue.Customer.FiscalCode);
                if (customer == null)
                {
                    customer = new TableCustomer()
                    {
                        AddressId = address.Id,
                        FiscalCode = importValue.Customer.FiscalCode,
                        Name = importValue.Customer.Name,
                    };
                    Manager.Context.TableCustomer.Add(customer);
                    await Manager.Context.SaveChangesAsync();
                }
                var insurance = await Manager.Context.TableInsurance.FirstOrDefaultAsync(x => x.ExternalId == importValue.Insurance.ExternalId);
                if (insurance == null)
                {
                    insurance = new TableInsurance()
                    {
                        CustomerId = customer.Id,
                        ExternalId = importValue.Insurance.ExternalId,
                        InsuranceCompanyId = importValue.Insurance.InsuranceCompanyId,
                    };
                    Manager.Context.TableInsurance.Add(insurance);
                    await Manager.Context.SaveChangesAsync();
                }
                accident = new TableAccident()
                {
                    ExternalId = importValue.Accident.ExternalId,
                    DenunciationDate = importValue.Accident.DenunciationDate,
                    AccidentDate = importValue.Accident.AccidentDate,
                    InsuranceId = insurance.Id,
                };
                Manager.Context.TableAccident.Add(accident);
                await Manager.Context.SaveChangesAsync();
            }
            var expertise = await Manager.Context.TableExpertise.FirstOrDefaultAsync(x => x.AccidentId == accident.Id);
            if (expertise == null)
            {
                expertise = new TableExpertise()
                {
                    AccidentId = accident.Id,
                    BranchId = importValue.Expertise.BranchId,
                    Status = importValue.Expertise.Status
                };
                Manager.Context.TableExpertise.Add(expertise);
                await Manager.Context.SaveChangesAsync();
            }
            foreach (var importedFile in importValue.Files)
            {
                using (var stream = new MemoryStream(importedFile.Content))
                {
                    stream.Position = 0;
                    await Manager.FileManager.SaveFile(expertise.Id, stream, importedFile.Name, importedFile.ContentType, $"insurancecompany/{importedFile.Name}");
                }
            }
            return accident.Id;
        }
    }
}