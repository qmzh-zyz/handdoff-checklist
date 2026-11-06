using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using System.Xml.Serialization;

public class Rule {
    public string Name, Keywords, Extensions;
    public int Minimum;
    public bool Matches(string path) {
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path).TrimStart('.');
        return Parts(Extensions).Contains(ext, StringComparer.OrdinalIgnoreCase)
            && Parts(Keywords).Any(k => name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
    }
    public static string[] Parts(string value) {
        return (value ?? "").Split(',', '，', ';', '；').Select(x => x.Trim().TrimStart('.')).Where(x => x.Length > 0).ToArray();
    }
}
public class Template {
    public string Name;
    public List<Rule> Rules;
    public override string ToString() { return Name; }
}
class Check {
    public string[] Files;
    public int[][] Matches;
    public int[] Choices;
    public Template Template;
    public Check(string folder, Template template) {
        Template = template;
        Files = Directory.GetFiles(folder).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToArray();
        Matches = Files.Select(file => template.Rules.Select((r,i) => new {r,i}).Where(x => x.r.Matches(file)).Select(x => x.i).ToArray()).ToArray();
        Choices = Enumerable.Repeat(-1, Files.Length).ToArray();
    }
    public int Category(int i) {
        return Choices[i] >= 0 ? Choices[i] : Matches[i].Length == 1 ? Matches[i][0] : -1;
    }
    public string Summary() {
        var counts = new int[Template.Rules.Count];
        int pending = 0, ambiguous = 0;
        for (int i=0; i<Files.Length; i++) {
            int category = Category(i);
            if (category >= 0) counts[category]++;
            else if (Matches[i].Length > 1) ambiguous++;
            else pending++;
        }
        var missing = Template.Rules.Select((r,i) => new {r,i}).Where(x => counts[x.i] < x.r.Minimum)
            .Select(x => x.r.Name + " " + (x.r.Minimum - counts[x.i]) + " 份").ToArray();
        return (missing.Length == 0 ? "必需材料已齐" : "缺少：" + string.Join("、", missing))
            + (pending > 0 ? "；待确认 " + pending + " 个" : "")
            + (ambiguous > 0 ? "；有歧义 " + ambiguous + " 个" : "");
    }
}
class App : Form {
    static readonly string Store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeliveryCheck");
    static readonly XmlSerializer Serializer = new XmlSerializer(typeof(Template));
    readonly ComboBox templates = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    readonly Label location = new Label { Dock = DockStyle.Top, Height = 30, AutoEllipsis = true, Text = "拖入一个文件夹，或点击「选择文件夹」。" };
    readonly Label result = new Label { Dock = DockStyle.Bottom, Height = 70 };
    readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    string folder;
    Check check;
    bool refreshing;
    static Template DefaultTemplate() {
        return new Template {Name="报销材料", Rules=new List<Rule> {
            new Rule {Name="发票", Keywords="发票,invoice", Extensions="pdf,jpg,jpeg,png,ofd", Minimum=1},
            new Rule {Name="付款截图", Keywords="付款,支付,回执", Extensions="jpg,jpeg,png,pdf", Minimum=1},
            new Rule {Name="报销单", Keywords="报销单", Extensions="pdf,xlsx,xls,docx,doc", Minimum=1}
        }};
    }
    static Button Button(string title, Action action) {
        var b = new Button {Text=title, AutoSize=true};
        b.Click += (s,e) => action();
        return b;
    }
    void Try(Action action) {
        try { action(); }
        catch(Exception ex) { MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    public App() {
        Text="交件清单"; ClientSize=new Size(880,510); MinimumSize=new Size(670,380);
        StartPosition=FormStartPosition.CenterScreen; Font=new Font("Microsoft YaHei UI",10);
        var bar=new FlowLayoutPanel {Dock=DockStyle.Top, Height=46, Padding=new Padding(4)};
        bar.Controls.Add(templates);
        bar.Controls.Add(Button("编辑并另存", () => Try(EditTemplate)));
        bar.Controls.Add(Button("选择文件夹", () => {
            using(var picker=new FolderBrowserDialog { ShowNewFolderButton=false }) {
                if(picker.ShowDialog(this)==DialogResult.OK) Try(() => Scan(picker.SelectedPath));
            }
        }));
        bar.Controls.Add(Button("打开文件", OpenSelected));
        bar.Controls.Add(Button("复制结果", () => { if(check!=null) Try(() => Clipboard.SetText(check.Summary())); }));
        Controls.Add(grid); Controls.Add(result); Controls.Add(location); Controls.Add(bar);
        foreach(Control drop in new Control[] {this,grid,location,bar,result}) {
            drop.AllowDrop=true;
            drop.DragEnter += (s,e) => {if(e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect=DragDropEffects.Copy;};
            drop.DragDrop += (s,e) => {
                var paths=e.Data.GetData(DataFormats.FileDrop) as string[];
                if(paths!=null && paths.Length==1 && Directory.Exists(paths[0])) Try(() => Scan(paths[0]));
                else MessageBox.Show(this,"请拖入一个文件夹。");
            };
        }
        grid.CurrentCellDirtyStateChanged += (s,e) => {
            if(grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewComboBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellValueChanged += (s,e) => {
            if(refreshing || check==null || e.RowIndex<0 || e.ColumnIndex!=2) return;
            int i=e.RowIndex;
            check.Choices[i]=Convert.ToInt32(grid.Rows[i].Cells[2].Value)-1;
            UpdateRow(i);
            result.Text=check.Summary();
        };
        grid.CellDoubleClick += (s,e) => { if(e.RowIndex>=0 && e.ColumnIndex==0) OpenSelected(); };
        templates.Items.Add(DefaultTemplate());
        if(Directory.Exists(Store)) foreach(string path in Directory.GetFiles(Store,"*.xml")) {
            Try(() => {using(var file=File.OpenRead(path)) templates.Items.Add((Template)Serializer.Deserialize(file));});
        }
        templates.SelectedIndex=0;
        templates.SelectedIndexChanged += (s,e) => {
            if(folder!=null) {
                string target=folder;
                folder=null; check=null; grid.Rows.Clear(); result.Text=""; location.Text=target;
                Try(() => Scan(target));
            }
        };
    }
    void OpenSelected() {
        if(check==null || grid.CurrentCell==null) return;
        string file=check.Files[grid.CurrentCell.RowIndex];
        Try(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) {UseShellExecute=true}));
    }
    void Scan(string path) {
        var next=new Check(path,(Template)templates.SelectedItem);
        refreshing=true;
        try {
            check=next; folder=path;
            grid.Columns.Clear();
            grid.Columns.Add(new DataGridViewTextBoxColumn {HeaderText="文件名",ReadOnly=true,SortMode=DataGridViewColumnSortMode.NotSortable,FillWeight=135});
            grid.Columns.Add(new DataGridViewTextBoxColumn {HeaderText="检查结果",ReadOnly=true,SortMode=DataGridViewColumnSortMode.NotSortable});
            var choices=new DataGridViewComboBoxColumn {HeaderText="手动归类", DisplayMember="Label",ValueMember="Id",SortMode=DataGridViewColumnSortMode.NotSortable};
            choices.DataSource=next.Template.Rules.Select((r,i) => new {Label=r.Name,Id=i+1})
                .Concat(new[] {new {Label="按规则判断",Id=0}}).OrderBy(x => x.Id).ToArray();
            grid.Columns.Add(choices);
            for(int i=0;i<next.Files.Length;i++) {
                grid.Rows.Add(Path.GetFileName(next.Files[i]),"",0);
                UpdateRow(i);
            }
            location.Text=path;
            result.Text=next.Summary();
        } finally {refreshing=false;}
    }
    void UpdateRow(int i) {
        int category=check.Category(i);
        grid.Rows[i].Cells[1].Value=category>=0
            ? (check.Choices[i]>=0 ? "手动 · " : "自动 · ") + check.Template.Rules[category].Name
            : check.Matches[i].Length>1
                ? "有歧义 · " + string.Join(" / ",check.Matches[i].Select(j => check.Template.Rules[j].Name))
                : "待确认";
    }
    void EditTemplate() {
        var original=(Template)templates.SelectedItem;
        using(var dialog=new Form {Text="另存为模板", ClientSize=new Size(760,330),StartPosition=FormStartPosition.CenterParent,Font=Font}) {
            using(var nameFont=new Font(Font.FontFamily,Font.Size+3,FontStyle.Bold))
            {
            var name=new TextBox {Dock=DockStyle.Top,Font=nameFont,Text=original.Name+"（副本）"};
            var help=new Label {Dock=DockStyle.Top, Height=30,Text="填写新模板名称。关键词和后缀用逗号分隔，关键词任一命中即可。"};
            var rules=new DataGridView {Dock=DockStyle.Fill,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill};
            rules.Columns.Add("category","材料名称");
            using(var categoryFont=new Font(Font.FontFamily, Font.Size+2)) {
                rules.Columns["category"].HeaderCell.Style.Font=categoryFont;
                rules.Columns["category"].DefaultCellStyle.Font=categoryFont;
                rules.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.AutoSize;
                rules.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells;
                rules.Columns.Add("keywords","文件名关键词");
                rules.Columns.Add("extensions","后缀");
                rules.Columns.Add("minimum","最低数量");
                foreach(var r in original.Rules) rules.Rows.Add(r.Name,r.Keywords,r.Extensions,r.Minimum);
                var save=Button("保存模板", () => Try(() => {
                    if(!rules.EndEdit()) return;
                    string title=name.Text.Trim();
                    if(title.Length==0 || templates.Items.Cast<Template>().Any(t => string.Equals(t.Name,title,StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("请输入一个新的模板名称。");
                    var list=new List<Rule>();
                    var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach(DataGridViewRow row in rules.Rows) {
                        if(row.IsNewRow) continue;
                        var values=row.Cells.Cast<DataGridViewCell>().Select(c => Convert.ToString(c.Value).Trim()).ToArray();
                        int minimum;
                        if(values[0]=="" || !names.Add(values[0]) || Rule.Parts(values[1]).Length==0 ||
                            Rule.Parts(values[2]).Length==0 || !int.TryParse(values[3],out minimum) || minimum<0)
                            throw new InvalidOperationException("请检查第 "+(row.Index+1)+" 行。材料名称须唯一，关键词和后缀不能为空，最低数量须为非负整数（0 表示可选）。");
                        list.Add(new Rule {Name=values[0],Keywords=values[1],Extensions=values[2],Minimum=minimum});
                    }
                    if(list.Count==0) throw new InvalidOperationException("模板至少需要一项材料。");
                    var template=new Template {Name=title,Rules=list};
                    Directory.CreateDirectory(Store);
                    string path=Path.Combine(Store,Guid.NewGuid().ToString("N"));
                    try {
                        using(var file=File.Create(path+".tmp")) Serializer.Serialize(file,template);
                        File.Move(path+".tmp",path+".xml");
                    } finally { if(File.Exists(path+".tmp")) File.Delete(path+".tmp"); }
                    dialog.DialogResult=DialogResult.OK;
                    templates.Items.Add(template);
                    templates.SelectedItem=template;
                }));
                rules.AllowUserToAddRows=false;
                rules.AllowUserToDeleteRows=false;
                rules.MultiSelect=false;
                var actions=new FlowLayoutPanel {Dock=DockStyle.Bottom, Height=42};
                actions.Controls.Add(Button("添加行", () => {
                    if(!rules.EndEdit()) return;
                    int index=rules.Rows.Add();
                    rules.CurrentCell=rules.Rows[index].Cells[0];
                    rules.Focus();
                    rules.BeginEdit(true);
                }));
                actions.Controls.Add(Button("删除选中行", () => {
                    if(rules.CurrentRow==null || !rules.EndEdit()) return;
                    var row=rules.CurrentRow;
                    bool hasContent=row.Cells.Cast<DataGridViewCell>()
                        .Any(cell => !string.IsNullOrWhiteSpace(Convert.ToString(cell.Value)));
                    if(hasContent && MessageBox.Show(dialog,"确定删除当前选中行？","删除材料规则",
                        MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes) return;
                    rules.Rows.Remove(row);
                }));
                actions.Controls.Add(save);
                dialog.Controls.Add(rules); dialog.Controls.Add(actions); dialog.Controls.Add(name); dialog.Controls.Add(help);
                dialog.ShowDialog(this);
            }
            }
        }
    }
    [STAThread]
    public static int Main(string[] args) {
        if(args.Length==1 && args[0]=="--self-test") return SelfTest();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new App());
        return 0;
    }
    static int SelfTest() {
        string root=Path.Combine(Path.GetTempPath(),"deliverycheck-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            foreach(string name in new[] {"发票.PDF","IMG_1234.jpg","发票付款.pdf"}) File.WriteAllText(Path.Combine(root,name),"");
            string child=Directory.CreateDirectory(Path.Combine(root,"子目录")).FullName;
            File.WriteAllText(Path.Combine(child,"报销单.pdf"),"");
            var test=new Check(root,DefaultTemplate());
            if(test.Files.Length!=3 || test.Summary()!="缺少：付款截图 1 份、报销单 1 份；待确认 1 个；有歧义 1 个") return 1;
            test.Choices[Array.FindIndex(test.Files,f => Path.GetFileName(f)=="IMG_1234.jpg")]=1;
            if(test.Summary()!="缺少：报销单 1 份；有歧义 1 个") return 2;
            test.Template.Rules[2].Minimum=0;
            if(test.Summary()!="必需材料已齐；有歧义 1 个") return 5;
            using(var stream=new MemoryStream()) {
                Serializer.Serialize(stream,test.Template);
                stream.Position=0;
                if(((Template)Serializer.Deserialize(stream)).Rules.Count!=3) return 3;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using(var app=new App()) {
                app.Show();
                app.Scan(root);
                int i=Array.FindIndex(app.check.Files,f => Path.GetFileName(f)=="IMG_1234.jpg");
                app.grid.Rows[i].Cells[2].Value=2;
                if(app.result.Text!="缺少：报销单 1 份；有歧义 1 个") return 4;
                app.Close();
            }
            return 0;
        } finally { File.Delete(Path.Combine(childPath(root),"报销单.pdf")); Directory.Delete(childPath(root)); foreach(string file in Directory.GetFiles(root)) File.Delete(file); Directory.Delete(root); }
    }
    static string childPath(string root) {return Path.Combine(root,"子目录");}
}
