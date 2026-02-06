# 📦 SLVZ Db, Simple Local File Database for C#

A lightweight, dependency-free library for storing and retrieving data locally in a file.  
This works like a very simplified ORM (similar to EF Core) but stores data in a file — perfect for small projects, configs, or apps that don’t require a real database.


## 1. Database context (Fast and good for millions of records)

### First lets create a model

```csharp
using SLVZ.Db;

enum Role
{
    Admin,
    User
}

class myModel
{
    [PrimaryKey] //Defines the unique key used by the database to identify the record
    public Int64 ID { get; set; }
    
    public string Name { get; set; }
    public Role Role { get; set; }
    public double Grade { get; set; }
    public bool IsActive { get; set; }
}

```
---

Attribute `PrimaryKey` is necessary

### Create Your Database Context
```csharp
using SLVZ.Db;

class myDB : DbContext
{
    public override void Configuration()
    {
        // Argument: Database file full path
        Initialize($"{Environment.CurrentDirectory}/database.db");
        Mdl1 = DataSet<myModel1>();
        Mdl1 = DataSet<myModel2>();
        Mdl1 = DataSet<myModel3>();

    }

    public DataSet<myModel1>? Mdl1;
    public DataSet<myModel2>? Mdl2;
    public DataSet<myModel3>? Mdl3;
}

```
---

### Example Usage

```csharp

using SLVZ.Db;


var db = new myDB();


var id_1 = await db.Mdl1.Add(new myModel1(...));
var id_2 = await db.Mdl2.Add(new myModel2(...));
var id_3 = await db.Mdl3.Add(new myModel3(...));


//Get all records
var models = await db.Mdl1.All();

//Find a record
var model = await db.Mdl1.Find([Int64.PrimaryKey]);

//Find a range of primary keys
var model = await db.Mdl1.Find(x => x.ID > 10 && x.ID < 20);

//Remove a record
await db.Mdl1.Remove([Int64.PrimaryKey]);
await db.Mdl1.Remove(YourModel);

//Remove a list of records
await db.Mdl1.Remove(ListModels);

//Update a record
await db.Mdl1.Update(YourModel);

//Update a list of records
await db.Mdl1.Update(ListModels);


//Filter records based of given condition
var models = await db.Mdl1.Where(x => x.Name.Contains("Text"));

//Determines whether any record matches the given condition
bool result = await db.Mdl1.Any(x => x.Name.Contains("Text"));

```
---



## 2. Light database context (Light and good for small jobs)

### First lets create a model

```csharp
using SLVZ.Db.Light;

enum Role
{
    Admin,
    User
}

class myModel
{
    [Key] //Defines the unique key used by the database to identify the record
    public int ID { get; set; }
    
    public string Name { get; set; }
    public Role Role { get; set; }
    public double Grade { get; set; }
    public bool IsActive { get; set; }
}

```
---

Attribute `Key` is necessary


### Create Your Database Context
```csharp

using SLVZ.Db.Light;

class myDB : LightContext
{
    public override void Configuration()
    {
        Mdl1 = DataSet<myModel1>("{Path}/model1.db");
        Mdl1 = DataSet<myModel2>("{Path}/model2.db");
        Mdl1 = DataSet<myModel3>("{Path}/model3.db");

        // Argument: Database file full path
    }

    public DataSet<myModel1>? Mdl1;
    public DataSet<myModel2>? Mdl2;
    public DataSet<myModel3>? Mdl3;
}

```
---


### Example Usage

```csharp

using SLVZ.Db.Light;


var db = new myDB();


await db.Mdl1.Add(new myModel1(...));
await db.Mdl2.Add(new myModel2(...));
await db.Mdl3.Add(new myModel3(...));

//Add a list of models
await db.Mdl1.Add(new List<myModel1>());

//Get all records
var models = await db.Mdl1.All();

//Find a record
var model = await db.Mdl1.Find("Your key");

//Remove a record
await db.Mdl1.Remove("Your key");
await db.Mdl1.Remove(YourModel);

//Remove a list of records
await db.Mdl1.RemoveRange(ListModels);

//Update a record
await db.Mdl1.Update(YourModel);

//Update a list of records
await db.Mdl1.UpdateRange(ListModels);


//Filter records based of given condition
var models = await db.Mdl1.Where(x => x.Name.Contains("Text"));

//Determines whether any record matches the given condition
bool result = await db.Mdl1.Any(x => x.Name.Contains("Text"));

```
---




👨‍💻 **Author:** [SLVZ](https://slvz.dev)
