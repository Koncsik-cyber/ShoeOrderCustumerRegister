﻿using KORCore.Utility;
using Newtonsoft.Json;
using SQLite;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using KORCore.Modules.Database.Models;
using KORCore.Modules.Database.Utility;
using DocumentFormat.OpenXml.Drawing.Charts;
using System.IO;
using System.Threading;


namespace KORCore.Modules.Database.Services
{
    public class LocalDatabaseModel : ILocalDatabase
    {
        private SQLiteAsyncConnection Database;
        private SQLiteConnectionString options;

        private static string CUSTOMER_TABLE = "Customers";
        private static string ORDER_TABLE = "Orders";
        private static string FILES_TABLE = "Files";
        private static string CONNECTION_DATA = "ConnectionDeviceDatas";

        private static int PAGE_SIZE = 5;

        public LocalDatabaseModel()
        {
            ThreadManager.Run(async () => await Init(), ThreadManager.Priority.High).Wait();
        }

        //public static event Action<string, object> OnDatabaseChange;

        private async Task Init(bool force = false)
        {
            if (Database is not null && !force)
            {
                return;
            }
            try
            {
                Database = new SQLiteAsyncConnection(Constants.basePath, Constants.Flags);
                await Database.CreateTableAsync<CustomerModel>();
                await Database.CreateTableAsync<OrderModel>();
                await Database.CreateTableAsync<FileModel>();
                await Database.CreateTableAsync<ConnectionDeviceData>();
                options = new SQLiteConnectionString(Constants.basePath, false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }


        }

        #region CustomerModel CRUD Implementation
        public async Task<int> CreateCustomer(CustomerModel customer)
        {
            CustomerModel? result = await GetCustomerById(customer.Guid);
            if (result == null)
            {
                IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.CUSTOMER_CREATED, customer);
                return await Database.InsertAsync(customer);
            }
            else
            {
                IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.CUSTOMER_UPDATED, customer);
                return await UpdateCustomer(customer);
            }
        }
        public async Task<CustomerModel> GetCustomerById(Guid id)
        {
            string stringId = id.ToString();
            var customer = await Database.FindAsync<CustomerModel>(stringId);
            if (customer != null)
            {
                var orders = await Database.Table<OrderModel>()
                    .Where(o => o.CustomerId.Equals(stringId))
                    .ToListAsync();
                foreach (var order in orders)
                {
                    order.Files = await GetFilesByOrderIdWithOutContent(order.Guid);
                }
                customer.Orders = orders;
            }
            IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.CUSTOMER_RETRIEVED, customer);
            return customer;
        }


        public async Task<List<CustomerModel>> GetAllCustomers(int page = int.MinValue)
        {
            if (page == int.MinValue)
            {
                var customers = await Database.Table<CustomerModel>().ToListAsync();
                foreach (var customer in customers)
                {
                    var orders = await Database.Table<OrderModel>()
                        .Where(o => o.CustomerId == customer.Id)
                        .ToListAsync();
                    foreach (var order in orders)
                    {
                        order.Files = await GetFilesByOrderIdWithOutContent(order.Guid);
                    }
                    customer.Orders = orders;
                }
                IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.CUSTOMERS_RETRIEVED, customers);
                return customers;
            }
            else
            {
                var customers = await Database.Table<CustomerModel>()
                    .ThenBy(c => c.Name)
                    .Skip((page - 1) * PAGE_SIZE)
                    .Take(PAGE_SIZE)
                    .ToListAsync();
                foreach (var customer in customers)
                {
                    var orders = await Database.Table<OrderModel>()
                        .Where(o => o.CustomerId == customer.Id)
                        .ToListAsync();
                    foreach (var order in orders)
                    {
                        order.Files = await GetFilesByOrderIdWithOutContent(order.Guid);
                    }
                    customer.Orders = orders;
                }
                IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.CUSTOMERS_RETRIEVED, customers);
                return customers;
            }
        }

        public async IAsyncEnumerable<CustomerModel> GetAllCustomersAsStream(CancellationToken cancellationToken)
        {
            var customers = await Database.Table<CustomerModel>().ToListAsync();
            foreach (var customer in customers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var orders = await Database.Table<OrderModel>()
                            .Where(o => o.CustomerId == customer.Id)
                            .ToListAsync();

                foreach (var order in orders)
                {
                    await foreach (FileModel file in GetFilesByOrderIdWithOutContentAsStream(order.Guid, cancellationToken))
                    {
                        if (file != null)
                        {
                            order.Files.Add(file);
                        }
                    }
                }
                customer.Orders = orders;
                IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.CUSTOMER_STREAM_RETRIEVED, customer);
                yield return customer;
            }
        }


        public async Task<int> UpdateCustomer(CustomerModel customer)
        {
            return await Database.UpdateAsync(customer);
        }

        public async Task<int> DeleteCustomer(Guid id)
        {
            string stringId = id.ToString();
            var customer = await GetCustomerById(id);
            if (customer != null)
            {
                foreach (var order in customer.Orders)
                {
                    await DeleteOrder(Guid.Parse(order.Id));
                }
                IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.CUSTOMER_DELETED, customer);
                return await Database.DeleteAsync(customer);
            }
            return 0;
        }

        public async Task<List<CustomerModel>> SearchCustomer(string search, int page = int.MinValue)
        {
            if (string.IsNullOrEmpty(search))
            {
                return await GetAllCustomers();
            }
            string likeQuery = $"%{search.Trim().ToLowerInvariant().Replace(" ", "%")}%";
            List<CustomerModel> customers = new List<CustomerModel>();

            if (page == int.MinValue)
            {
                string query = $@"SELECT * FROM {CUSTOMER_TABLE} 
                       WHERE LOWER(Name) LIKE ? OR 
                       LOWER(Address) LIKE ? OR
                       LOWER(Phone) LIKE ? OR
                       LOWER(Email) LIKE ? OR
                       LOWER(Note) LIKE ? OR
                       LOWER(NationalHealthInsurance) LIKE ?";
                customers = await Database.QueryAsync<CustomerModel>(query, likeQuery, likeQuery, likeQuery, likeQuery, likeQuery, likeQuery);
            }
            else
            {
                string query = $@"
                      SELECT * FROM {CUSTOMER_TABLE}
                      WHERE LOWER(Name) LIKE ? OR 
                      LOWER(Address) LIKE ? OR
                      LOWER(Phone) LIKE ? OR
                      LOWER(Email) LIKE ? OR
                      LOWER(Note) LIKE ? OR
                      LOWER(NationalHealthInsurance) LIKE ?
                      LIMIT ?
                      OFFSET ?";
                customers = await Database.QueryAsync<CustomerModel>(query, likeQuery, likeQuery, likeQuery,
                     likeQuery, likeQuery, likeQuery, PAGE_SIZE, PAGE_SIZE * (page - 1));
            }
            List<CustomerModel> customersOrders = customers
                .GroupBy(c => c.Id)
                .Select(g => g.First())
                .ToList();
            IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.CUSTOMERS_RETRIEVED, customersOrders);
            return customersOrders;

        }

        public async IAsyncEnumerable<CustomerModel> SearchCustomerAsStream(string search, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(search))
            {
                await foreach (var customer in GetAllCustomersAsStream(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return customer;
                }
            }
            string likeQuery = $"%{search.Trim().ToLowerInvariant().Replace(" ", "%")}%";
            string query = $@"SELECT * FROM {CUSTOMER_TABLE} 
                       WHERE LOWER(Name) LIKE ? OR 
                       LOWER(Address) LIKE ? OR
                       LOWER(Phone) LIKE ? OR
                       LOWER(Email) LIKE ? OR
                       LOWER(Note) LIKE ? OR
                       LOWER(NationalHealthInsurance) LIKE ?";
            List<CustomerModel> customers = await Database.QueryAsync<CustomerModel>(query, likeQuery, likeQuery, likeQuery, likeQuery, likeQuery, likeQuery);

            foreach (var customer in customers.GroupBy(c => c.Id).Select(g => g.First()).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.CUSTOMER_STREAM_RETRIEVED, customer);
                yield return customer;
            }
        }

        public async Task<int> CountCustomers()
        {
            var count = await Database.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {CUSTOMER_TABLE}");
            IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.CUSTOMER_COUNT_CHANGED, count);
            return count;
        }

        #endregion
        #region OrderModel CRUD Implementation
        public async Task<int> CreateOrder(OrderModel order)
        {
            OrderModel? result = await GetOrderById(order.Guid);
            if (result == null)
            {
                IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.ORDER_CREATED, order);
                return await Database.InsertAsync(order);
            }
            else
            {
                IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.ORDER_UPDATED, order);
                return await UpdateOrder(order);
            }
        }

        public async Task<OrderModel> GetOrderById(Guid id)
        {
            string stringId = id.ToString();
            var order = await Database.FindAsync<OrderModel>(stringId);
            if (order != null)
            {
                order.Files = order.Files = await GetFilesByOrderIdWithOutContent(order.Guid);
            }
            IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.ORDER_RETRIEVED, order);
            return order;
        }

        public async Task<List<OrderModel>> GetAllOrders(int page = int.MinValue)
        {
            List<OrderModel> orders = new List<OrderModel>();
            if (page == int.MinValue)
            {
                orders = await Database.Table<OrderModel>()
                    .OrderBy(o => o.StartDate)
                    .ToListAsync();
            }
            else
            {
                orders = await Database.Table<OrderModel>()
                    .ThenBy(o => o.StartDate)
                    .Skip((page - 1) * PAGE_SIZE)
                    .Take(PAGE_SIZE).ToListAsync();
            }
            var tasks = orders.Select(order =>
                    ThreadManager.Run(async () =>
                    {
                        var fileCount = await Database.Table<FileModel>()
                        .Where(f => f.OrderId.Equals(order.Id))
                        .CountAsync();
                        if (fileCount > 0)
                        {
                            order.Files = order.Files = await GetFilesByOrderIdWithOutContent(order.Guid);
                        }
                        if (!string.IsNullOrEmpty(order.CustomerId))
                        {
                            order.Customer = await GetCustomerById(Guid.Parse(order.CustomerId));
                        }
                    }, ThreadManager.Priority.High));

            await Task.WhenAll(tasks);
            IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.ORDERS_RETRIEVED, orders);
            return orders;
        }



        public async IAsyncEnumerable<OrderModel> GetAllOrdersAsStream(CancellationToken cancellationToken)
        {
            List<OrderModel> orders = await Database.Table<OrderModel>()
                    .OrderBy(o => o.StartDate)
                    .ToListAsync();
            foreach (var order in orders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileCount = await Database.Table<FileModel>()
                    .Where(f => f.OrderId.Equals(order.Id))
                    .CountAsync();
                if (fileCount > 0)
                {
                    order.Files = order.Files = await GetFilesByOrderIdWithOutContent(order.Guid);
                }
                if (!string.IsNullOrEmpty(order.CustomerId))
                {
                    order.Customer = await GetCustomerById(Guid.Parse(order.CustomerId));
                }
                IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.ORDER_STREAM_RETRIEVED, order);
                yield return order;
            }
        }

        public async Task<int> UpdateOrder(OrderModel order)
        {
            return await Database.UpdateAsync(order);
        }

        public async Task<int> DeleteOrder(Guid id)
        {
            string stringId = id.ToString();
            var order = await GetOrderById(id);
            if (order != null)
            {
                foreach (var file in order.Files)
                {
                    await DeleteFile(Guid.Parse(file.Id));
                }
                IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.ORDER_DELETED, order);
                return await Database.DeleteAsync(order);
            }
            return 0;
        }

        public async Task<List<OrderModel>> SearchOrders(string search, int page = int.MinValue)
        {
            if (string.IsNullOrEmpty(search))
            {
                return await GetAllOrders();
            }
            string likeQuery = $"%{search.Trim().ToLowerInvariant().Replace(" ", "%")}%";
            List<OrderModel> orders = new List<OrderModel>();


            if (page == int.MinValue)
            {
                var query = $@"SELECT o.* FROM {ORDER_TABLE} o
                           JOIN {CUSTOMER_TABLE} c ON o.CustomerId = c.Id
                           WHERE LOWER(o.OrderNumber) LIKE ? OR 
                           LOWER(o.Note) LIKE ? OR
                           LOWER(c.Name) LIKE ? OR
                           LOWER(c.Address) LIKE ? OR
                           LOWER(c.Phone) LIKE ? OR
                           LOWER(c.Email) LIKE ? OR
                           LOWER(c.NationalHealthInsurance) LIKE ?";


                orders = await Database.QueryAsync<OrderModel>(query, likeQuery, likeQuery, likeQuery, likeQuery, likeQuery, likeQuery, likeQuery);
            }
            else
            {
                var query = $@"SELECT o.* FROM {ORDER_TABLE} o
                           JOIN {CUSTOMER_TABLE} c ON o.CustomerId = c.Id
                           WHERE LOWER(o.OrderNumber) LIKE ? OR 
                           LOWER(o.Note) LIKE ? OR
                           LOWER(c.Name) LIKE ? OR
                           LOWER(c.Address) LIKE ? OR
                           LOWER(c.Phone) LIKE ? OR
                           LOWER(c.Email) LIKE ? OR
                           LOWER(c.NationalHealthInsurance) LIKE ?
                           LIMIT ?
                           OFFSET ?";


                orders = await Database.QueryAsync<OrderModel>(query, likeQuery,
                    likeQuery, likeQuery, likeQuery, likeQuery, likeQuery, likeQuery,
                    PAGE_SIZE, PAGE_SIZE * (page - 1));
            }

            orders = orders
                .GroupBy(c => c.Id)
                .Select(g => g.First())
                .ToList();

            ConcurrentBag<Task> tasks = new ConcurrentBag<Task>();

            void RunBackgroundTask(OrderModel order)
            {
                tasks.Add(ThreadManager.Run(async () =>
                {
                    if (!string.IsNullOrEmpty(order.CustomerId))
                    {
                        order.Customer = await GetCustomerById(Guid.Parse(order.CustomerId));
                        order.Files = await GetFilesByOrderIdWithOutContent(order.Guid);
                    }
                }, ThreadManager.Priority.High));
            }

            orders.AsParallel().ForAll(order => RunBackgroundTask(order));
            await Task.WhenAll(tasks);
            IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.ORDERS_RETRIEVED, orders);
            return orders;
        }

        public async IAsyncEnumerable<OrderModel> SearchOrdersAsStream(string search, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(search))
            {
                await foreach (var order in GetAllOrdersAsStream(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return order;
                }
            }
            string likeQuery = $"%{search.Trim().ToLowerInvariant().Replace(" ", "%")}%";
            var query = $@"SELECT o.* FROM {ORDER_TABLE} o
                        JOIN {CUSTOMER_TABLE} c ON o.CustomerId = c.Id
                        WHERE LOWER(o.OrderNumber) LIKE ? OR 
                        LOWER(o.Note) LIKE ? OR
                        LOWER(c.Name) LIKE ? OR
                        LOWER(c.Address) LIKE ? OR
                        LOWER(c.Phone) LIKE ? OR
                        LOWER(c.Email) LIKE ? OR
                        LOWER(c.NationalHealthInsurance) LIKE ?";

            List<OrderModel> orders = await Database.QueryAsync<OrderModel>(query, likeQuery, likeQuery, likeQuery, likeQuery, likeQuery, likeQuery, likeQuery);

            foreach (var order in orders.GroupBy(c => c.Id).Select(g => g.First()).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(order.CustomerId))
                {
                    order.Customer = await GetCustomerById(Guid.Parse(order.CustomerId));
                    order.Files = await GetFilesByOrderIdWithOutContent(order.Guid);
                }
                IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.ORDER_STREAM_RETRIEVED, order);
                yield return order;
            }
        }

        public async Task<int> CountOrders()
        {
            var count = await Database.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {ORDER_TABLE}");
            IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.ORDER_COUNT_CHANGED, count);
            return count;
        }
        #endregion
        #region FileModel CRUD Implementation
        public async Task<int> CreateFile(FileModel file)
        {
            FileModel? result = await GetFileById(file.Guid);
            if (result == null)
            {
                IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.FILE_CREATED, file);
                return await Database.InsertAsync(file);
            }
            else
            {
                IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.FILE_UPDATED, file);
                return await UpdateFile(file);
            }
        }

        public async Task<FileModel> GetFileById(Guid id)
        {
            string stringId = id.ToString();
            FileModel file = await Database.FindAsync<FileModel>(stringId);
            IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.FILE_RETRIEVED, file);
            return file;
        }

        public async Task<List<FileModel>> GetAllFilesByOrderId(Guid id)
        {
            string stringId = id.ToString();
            var fileCount = await Database.Table<FileModel>()
                .Where(f => f.OrderId.Equals(stringId))
                .CountAsync();
            if (fileCount > 0)
            {
                List<FileModel> files = await Database.Table<FileModel>()
                    .Where(f => f.OrderId.Equals(stringId))
                    .ToListAsync();
                IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.FILE_RETRIEVED, files);
                return files;
            }
            IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.FILE_RETRIEVED, new List<FileModel>());
            return new List<FileModel>();
        }

        public async IAsyncEnumerable<FileModel> GetAllFilesByOrderIdAsStream(Guid id, CancellationToken cancellationToken)
        {
            string stringId = id.ToString();
            var fileCount = await Database.Table<FileModel>()
                .Where(f => f.OrderId.Equals(stringId))
                .CountAsync();
            if (fileCount > 0)
            {
                foreach (var file in await Database.Table<FileModel>().Where(f => f.OrderId.Equals(stringId)).ToListAsync())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.FILE_STREAM_RETRIEVED, file);
                    yield return file;
                }
            }
            yield return null;
        }

        public async Task<List<FileModel>> GetAllFiles()
        {
            List<FileModel> files = await Database.Table<FileModel>().ToListAsync();
            files.AsParallel().Where(f => f.IsDatabaseContent = true);
            IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.FILES_RETRIEVED, files);
            return files;
        }

        public async IAsyncEnumerable<FileModel> GetAllFilesAsStream(CancellationToken cancellationToken)
        {
            List<FileModel> files = await Database.Table<FileModel>().ToListAsync();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                file.IsDatabaseContent = true;
                file.Order = await GetOrderById(Guid.Parse(file.OrderId));
                IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.FILE_STREAM_RETRIEVED, files);
                yield return file;
            }
        }

        public async Task<int> UpdateFile(FileModel file)
        {
            if(string.IsNullOrEmpty(file.ContentBase64))
            {
                FileModel dbFile = await GetFileById(file.Guid);
                file.Content = dbFile.Content;
                file.OrderId = dbFile.OrderId;
            }
            return await Database.UpdateAsync(file);
        }

        public async Task<int> DeleteFile(Guid id)
        {

            string stringId = id.ToString();
            var file = await GetFileById(id);
            if (file != null)
            {
                int reuslt = await Database.DeleteAsync(file);
                IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.FILE_DELETED, reuslt);
                return reuslt;
            }
            return 0;
        }

        public async Task<List<FileModel>> GetFilesByOrderIdWithOutContent(Guid id)
        {
            var query = $"SELECT id, orderId, name, contentType, note, hashCode FROM {FILES_TABLE} WHERE orderId = ?";
            List<FileModel> fileModels = await Database.QueryAsync<FileModel>(query, id.ToString());
            foreach (var file in fileModels)
            {
                file.IsDatabaseContent = true;
            }
            IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.FILES_RETRIEVED, fileModels);
            return fileModels;
        }

        public async IAsyncEnumerable<FileModel> GetFilesByOrderIdWithOutContentAsStream(Guid id, CancellationToken cancellationToken)
        {
            var query = $"SELECT id, orderId, name, contentType, note, hashCode FROM {FILES_TABLE} WHERE orderId = ?";
            foreach (var file in await Database.QueryAsync<FileModel>(query, id.ToString()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                file.IsDatabaseContent = true;
                IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.FILE_STREAM_RETRIEVED, file);
                yield return file;
            }
        }

        public async Task<string> GetFileContentSize(Guid id)
        {
            var query = $"SELECT SUM(length(content)) FROM {FILES_TABLE} WHERE id = ?";
            long length = await Database.ExecuteScalarAsync<long>(query, id.ToString());
            IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.FILE_SIZE, length.ToStringSize());
            return length.ToStringSize();
        }
        public async Task<int> CountFiles()
        {
            var count = await Database.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {FILES_TABLE}");
            IDatabaseModel.InvokeOnDatabaseChange(DatabaseChangedType.FILE_COUNT, count);
            return count;
        }
        #endregion
        #region export/import
        public async Task ExportDatabaseToJson(string filePath, CancellationToken cancellationToken, Action<float> progressCallback = null)
        {
            int totalItems = await CountCustomers() + await CountOrders() + await CountFiles();
            int processedItems = 0;

            using (FileStream fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
            using (StreamWriter streamWriter = new StreamWriter(fileStream, new UTF8Encoding(false)))
            using (JsonTextWriter jsonWriter = new JsonTextWriter(streamWriter))
            {
                JsonSerializer serializer = new JsonSerializer();

                jsonWriter.WriteStartObject();
                // Serialize Customers
                jsonWriter.WritePropertyName(CUSTOMER_TABLE);
                jsonWriter.WriteStartArray();
                await foreach (var customer in GetAllCustomersAsStream(cancellationToken))
                {
                    serializer.Serialize(jsonWriter, customer);
                    processedItems++;
                    progressCallback?.Invoke(100 * processedItems / totalItems);
                }
                jsonWriter.WriteEndArray();

                // Serialize Orders
                jsonWriter.WritePropertyName(ORDER_TABLE);
                jsonWriter.WriteStartArray();
                await foreach (var order in GetAllOrdersAsStream(cancellationToken))
                {
                    serializer.Serialize(jsonWriter, order);
                    processedItems++;
                    progressCallback?.Invoke(100 * processedItems / totalItems);
                }
                jsonWriter.WriteEndArray();

                // Serialize Files
                jsonWriter.WritePropertyName(FILES_TABLE);
                jsonWriter.WriteStartArray();
                await foreach (var file in GetAllFilesAsStream(cancellationToken))
                {
                    serializer.Serialize(jsonWriter, file);
                    processedItems++;
                    progressCallback?.Invoke(100 * processedItems / totalItems);
                }
                jsonWriter.WriteEndArray();

                // Serialize ConnectionDeviceDatas
                jsonWriter.WritePropertyName(CONNECTION_DATA);
                jsonWriter.WriteStartArray();
                await foreach (var connectionData in GetConnectionDataAsStreamAsync(cancellationToken))
                {
                    serializer.Serialize(jsonWriter, connectionData);
                    processedItems++;
                    progressCallback?.Invoke(100 * processedItems / totalItems);
                }
                jsonWriter.WriteEndArray();

                jsonWriter.WriteEndObject();
                await jsonWriter.FlushAsync();
            }
        }

        public async Task ImportDatabaseFromJson(Stream jsonStream, Action<float> progressCallback = null)
        {
            ProgressState state = new ProgressState(jsonStream.Length);
            using (StreamReader streamReader = new StreamReader(jsonStream))
            using (JsonTextReader jsonReader = new JsonTextReader(streamReader))
            {
                JsonSerializer serializer = new JsonSerializer();

                if (!jsonReader.Read() || jsonReader.TokenType != JsonToken.StartObject)
                {
                    throw new JsonSerializationException("Expected start of JSON object.");
                }
                await Database.DropTableAsync<CustomerModel>();
                await Database.DropTableAsync<OrderModel>();
                await Database.DropTableAsync<FileModel>();
                await Database.DropTableAsync<ConnectionDeviceData>();
                await Init(force: true);

                while (jsonReader.Read())
                {
                    if (jsonReader.TokenType == JsonToken.PropertyName)
                    {
                        string propertyName = jsonReader.Value.ToString();
                        if (propertyName == CUSTOMER_TABLE)
                        {
                            state.StreamPosition = 10;
                            await ProcessItems<CustomerModel>(jsonReader, serializer, CreateCustomer, state, progressCallback);
                            state.StreamPosition = 33;
                        }
                        else if (propertyName == ORDER_TABLE)
                        {
                            state.StreamPosition = 40;
                            await ProcessItems<OrderModel>(jsonReader, serializer, CreateOrder, state, progressCallback);
                            state.StreamPosition = 66;
                        }
                        else if (propertyName == FILES_TABLE)
                        {
                            state.StreamPosition = 70;
                            await ProcessItems<FileModel>(jsonReader, serializer, CreateFile, state, progressCallback);
                            state.StreamPosition = 59;
                        }
                        else if (propertyName == CONNECTION_DATA)
                        {
                            state.StreamPosition = 90;
                            await ProcessItems<ConnectionDeviceData>(jsonReader, serializer, CreateOrUpdateDatabaseConnection, state, progressCallback);
                            state.StreamPosition = 100;
                        }
                    }
                }
            }

        }

        private async Task ProcessItems<T>(JsonTextReader jsonReader, JsonSerializer serializer, Func<T, Task> createFunc, ProgressState state, Action<float> progressCallback)
        {
            if (jsonReader.Read() && jsonReader.TokenType == JsonToken.StartArray)
            {

                while (jsonReader.Read() && jsonReader.TokenType != JsonToken.EndArray)
                {
                    T item = serializer.Deserialize<T>(jsonReader);
                    await createFunc(item);
                    state.UpdateProgress(progressCallback);
                }
            }
        }

        public class DatabaseImportModel
        {
            public List<CustomerModel> Customers { get; set; }
            public List<OrderModel> Orders { get; set; }
            public List<FileModel> Files { get; set; }
        }

        #endregion
        #region Connection Data CRUD Implementation
        public async Task<int> CreateOrUpdateDatabaseConnection(ConnectionDeviceData connection)
        {
            ConnectionDeviceData? result = await Database.Table<ConnectionDeviceData>().FirstOrDefaultAsync(c => c.Url.Equals(connection.Url) && c.ServerKey.Equals(connection.ServerKey));

            if (result == null)
            {
                return await Database.InsertAsync(connection);
            }
            else
            {
                return await Database.UpdateAsync(connection);
            }
        }
        public async Task<int> DeleteConnection(ConnectionDeviceData connection)
        {
            ConnectionDeviceData? result = await GetConnectionById(connection.Guid);
            if (result == null)
            {
                return await Database.DeleteAsync(connection);
            }
            return 0;
        }
        public async IAsyncEnumerable<ConnectionDeviceData> GetConnectionDataAsStreamAsync(CancellationToken cancellationToken)
        {
            var connections = await Database.Table<ConnectionDeviceData>().ToListAsync();
            foreach (var connection in connections.AsParallel())
            {
                yield return connection;
            }
        }

        public async Task<ConnectionDeviceData> GetConnectionById(Guid id)
        {
            string stringId = id.ToString();
            var connection = await Database.FindAsync<ConnectionDeviceData>(stringId);
            return connection;
        }
        public async IAsyncEnumerable<ConnectionDeviceData> SearchConnectionDataAsStreamAsync(string searchValue, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(searchValue))
            {
                await foreach (var connectionData in GetConnectionDataAsStreamAsync(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return connectionData;
                }
            }

            string likeQuery = $"%{searchValue.Trim().ToLowerInvariant().Replace(" ", "%")}%";
            string query = @"SELECT * FROM ConnectionDeviceData 
                           WHERE LOWER(DeviceKey) LIKE ? OR 
                                 LOWER(ServerKey) LIKE ? OR
                                 LOWER(Url) LIKE ?";
            List<ConnectionDeviceData> connectionDataList = await Database.QueryAsync<ConnectionDeviceData>(query, likeQuery, likeQuery, likeQuery);

            foreach (var connectionData in connectionDataList.GroupBy(c => c.Id).Select(g => g.First()).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return connectionData;
            }
        }
        #endregion

    }


    internal static class Constants
    {
#if DEBUG
        public const string DatabaseFilename = "order_debug.db";
#else
        public const string DatabaseFilename = "order.db";
#endif

        public const SQLiteOpenFlags Flags =
            SQLiteOpenFlags.ReadWrite |
            SQLiteOpenFlags.Create |
            SQLiteOpenFlags.SharedCache;

        public static string basePath =>
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DatabaseFilename);
    }

    internal class ProgressState
    {
        //TODO: Implement ProgressState
        public long StreamPosition { get; set; }
        public long FileSize { get; }
        internal ProgressState(long fileSize)
        {
            FileSize = fileSize;
        }
        public void UpdateProgress(Action<float> progressCallback)
        {
            if (progressCallback != null)
            {
                progressCallback?.Invoke(StreamPosition);
            }

        }
    }

}
