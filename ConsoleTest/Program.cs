using SLVZ.Db;
using System.ComponentModel;

var db = new myDB();


var model1 = new myModel
{
    Grade = "A",
    ID = 1,
    Name = "Ashkan",
    Value = "Test1"
};
var model2 = new myModel
{
    Grade = "B",
    ID = 2,
    Name = "Mame",
    Value = "Test2"
};
var model3 = new myModel
{
    Grade = "C",
    ID = 3,
    Name = "Kos",
    Value = "Test3"
};
var model4 = new myModel
{
    Grade = "D",
    ID = 4,
    Name = "MameAsde",
    Value = "Test4ASvvvr"
};
var model5 = new myModel
{
    Grade = "E",
    ID = 5,
    Name = "Kooon",
    Value = "Test5"
};




var index1 = await db.models.Add(model1);

var index2 = await db.models.Add(model2);

var index3 = await db.models.Add(model3);

var index4 = await db.models.Add(model4);

var index5 = await db.models.Add(model5);

var models1 = await db.models.All();


await db.models.Remove(index3);
await db.models.Remove(index4);



var data2 = await db.models.Find(index1);


data2.Name = "Ashitela";
await db.models.Update(data2);

//await db.models.Remove(index1);
//await db.models.Remove(index5);
//await db.models.Remove(index4);
//await db.models.Remove(index3);




var models = await db.models.All();

Console.WriteLine($"Done.");
Console.ReadKey();




class myModel
{
    [PrimaryKey]
    public Int64 ID { get; set; }

    [Description]
    public string Name { get; set; }
    public string Value { get; set; }
    public string Grade { get; set; }
}


class myDB : DbContext
{
    public override void Configuration()
    {
        Initialize($"{Environment.CurrentDirectory}/slvz.db");
        models = DataSet<myModel>();
    }

    public DataSet<myModel>? models;
}