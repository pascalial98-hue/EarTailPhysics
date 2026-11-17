using System.Numerics;
using System.Text.Json;
using EarTailPhysics;

unsafe partial class Program
{
    static int checks;
    static void Assert(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static void Near(float a, float b, string why) => Assert(MathF.Abs(a-b)<.000002f, $"{why}: {a} != {b}");
    static byte* Chain(byte* h, int sim, int chain = 0) => *(byte**)(*(byte**)(h+0xD8)+104*sim+0x38)+56*chain;

    static void Main(string[] args)
    {
        using var memory = new NativeMemoryView();
        var h = memory.Allocate(0x120);
        h[0xD0] = 1; *(uint*)(h+0xAC)=1;
        var sim = memory.Allocate(104); *(byte**)(h+0xD8)=sim;
        sim[2]=1; sim[36]=0x13; sim[37]=14;
        var chain = memory.Allocate(56); *(byte**)(sim+0x38)=chain;
        *(ushort*)(chain+2)=5;
        var nodes=memory.Allocate(420); *(byte**)(chain+0x30)=nodes;
        string[] names=["n_sippo_b","n_sippo_c","n_sippo_d","n_sippo_e",""];
        for(int i=0;i<5;i++) { NativeMemoryView.SetName(nodes+i*84,names[i]); *(float*)(nodes+i*84+36)=1; }
        var caps=memory.Allocate(4*124); h[0xE8]=4; *(byte**)(h+0xF0)=caps;
        string[] anchors=["asi_l_a","asi_r_a","asi_l_c","asi_r_c"];
        for(int i=0;i<4;i++) NativeMemoryView.SetName(caps+i*124, anchors[i]);
        var originalHandle=new ReadOnlySpan<byte>(h,0x120).ToArray();
        var originalNodes=new ReadOnlySpan<byte>(nodes,420).ToArray();
        for(int type=0;type<3;type++) for(int strength=1;strength<=2;strength++)
        for(int ear=0;ear<4;ear++) for(int body=0;body<4;body++) for(int ground=0;ground<2;ground++)
        {
            var s=new Settings {TailType=type,TailStrength=strength,EarGrade=ear,BodyCollision=body,GroundCollision=ground==1};
            using var built=new DescriptorBuilder(h,s,true);
            Assert(built.HasTail&&built.HasEars,"Independent tail and ear creation");
            Assert(built.Handle[0xD0]==2,"Exactly one added ear simulator");
            var t=Chain(built.Handle,0);var n=*(byte**)(t+0x30);
            Near(*(float*)(t+4),Presets.Tails[type,strength-1].Damping,"Tail damping");
            Near(*(float*)(Chain(built.Handle,1)+4),Presets.Ears[ear].Damping,"Independent ear damping");
            Assert(new ReadOnlySpan<byte>(n,84).SequenceEqual(originalNodes.AsSpan(0,84)),"Root node preserved");
            Assert(*(uint*)(n+84+76)==0,"Proximal node excluded from body collision");
            Assert(*(uint*)(n+168+76)==(body==0?0u:31u),"Middle node contact mask");
            Assert((*(byte**)(built.Handle+0xD8))[37]==(ground==1?142:14),"Continuous contact group");
            Assert(new ReadOnlySpan<byte>(h,0x120).SequenceEqual(originalHandle),"Shared resource unchanged");
            Assert(new ReadOnlySpan<byte>(nodes,420).SequenceEqual(originalNodes),"Shared nodes unchanged");
        }
        using(var off=new DescriptorBuilder(h,new Settings{Enabled=false},true))
        {
            Assert(off.Handle[0xD0]==1&&!off.HasTail&&!off.HasEars,"Disabled preserves source layout");
            Assert(new ReadOnlySpan<byte>(*(byte**)(off.Handle+0xD8),104).SequenceEqual(new ReadOnlySpan<byte>(sim,104)),"Disabled exact descriptor bytes");
        }
        using(var onlyEars=new DescriptorBuilder(h,new Settings{TailStrength=0},true))
            Assert(!onlyEars.HasTail&&onlyEars.HasEars,"Ears independent of original tail");
        using(var onlyTail=new DescriptorBuilder(h,new Settings{Ears=false},true))
            Assert(onlyTail.HasTail&&!onlyTail.HasEars&&onlyTail.Handle[0xD0]==1,"Tail independent of ears");
        var point=new Vector3(1,-.1f,2);
        Assert(ContactMath.ProjectPlane(ref point,Vector3.Zero,Vector3.UnitY,.04f),"Ground projection");
        Near(point.Y,.04f,"Ground supports radius");
        var v=ContactMath.RemoveInwardVelocity(new Vector3(1,-2,3),Vector3.UnitY);
        Near(v.Y,0,"No rebound energy");Near(v.X,1,"Keep tangential inertia");
        var pts=new[]{new Vector3(0,0,0),new Vector3(0,1,0),new Vector3(5,2,0),new Vector3(6,3,0)};
        ContactMath.KeepLengths(pts,[1,1,1],2);
        Assert(pts[0]==Vector3.Zero&&pts[1]==Vector3.UnitY,"Root and proximal stay fixed");
        Near(Vector3.Distance(pts[1],pts[2]),1,"Chain length maintained");
        point=new(float.NaN,0,0);
        Assert(!ContactMath.ProjectPlane(ref point,Vector3.Zero,Vector3.UnitY,.04f),"Reject nonfinite contact");
        TestContacts();
        TestProfiles();
        TestNodeContacts();
        TestWorldContacts();
        TestPreviewGeometry();
        TestRuntimeBindingAndWind();
        int cases=0;
        if(args.Length>0)
        {
            foreach(var test in JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(args[0]))!)
            {
                using var fixture=new FileFixture(File.ReadAllBytes(test.Input));
                using var built=new DescriptorBuilder(fixture.Handle,new Settings{TailType=test.Type,TailStrength=test.Strength,EarGrade=test.Ear,BodyCollision=test.Body},test.Cat);
                using var expected=new FileFixture(File.ReadAllBytes(test.Expected));
                CompareChains(built.Handle,expected.Handle);
                cases++;
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new{passed=true,checks,mod_comparison_cases=cases,native_game_calls_tested=false}));
    }
    public record Case(string Input,string Expected,int Type,int Strength,int Ear,int Body,bool Cat);
    static void TestContacts()
    {
        using var mem=new NativeMemoryView();
        var module=mem.Allocate(0x590);var chain=mem.Allocate(0x180);var particles=mem.Allocate(5*96);
        *(ushort*)(chain+0xE8)=5;*(byte**)(chain+0x20)=particles;
        *(Matrix4x4*)(module+0x10)=Matrix4x4.CreateTranslation(100,10,-20);
        for(int i=0;i<5;i++)
        {
            var p=new Vector3(i*.2f,i<2?.12f:-.03f,0);
            *(Vector3*)(particles+i*96)=p;
            *(Vector3*)(particles+i*96+0x40)=p;
            *(Vector3*)(particles+i*96+0x20)=new Vector3(.01f,-.03f,.02f);
        }
        SceneContacts.RayDelegate ray=(nint _,SceneContacts.Hit* hit,Vector3* origin,Vector3* direction,float distance,int layers,int* flags)=>
        {
            hit->Point=new Vector3(origin->X,10,origin->Z);hit->Normal=Vector3.UnitY;hit->Distance=origin->Y-10;
            return hit->Distance>=0&&hit->Distance<=distance?(byte)1:(byte)0;
        };
        SceneContacts.SweepDelegate sweep=(nint _,SceneContacts.Hit* hit,Vector4* sphere,Vector3* direction,float distance,int layers,int* flags)=>0;
        var contacts=new SceneContacts(System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(ray),System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(sweep));
        var state=new SceneContacts.State((nint)module);
        Assert(contacts.Resolve(chain,state,new Settings{GroundCollision=true},1),"Ground resolves translated world coordinates");
        for(int i=2;i<5;i++)
        {
            Assert((*(Vector3*)(particles+i*96)).Y>=0,"Contact does not penetrate floor");
            Near((*(Vector3*)(particles+i*96+0x20)).Y,0,"Contact removes downward speed without bounce");
        }
        Near((*(Vector3*)particles).Y,.12f,"Ground leaves root unchanged");
        Near((*(Vector3*)(particles+96)).Y,.12f,"Ground leaves proximal node unchanged");
        var before=new ReadOnlySpan<byte>(particles,5*96).ToArray();
        Assert(!contacts.Resolve(chain,state,new Settings(),1),"No query when collisions disabled");
        Assert(new ReadOnlySpan<byte>(particles,5*96).SequenceEqual(before),"Disabled contact preserves native particles");
        Assert(!contacts.Resolve(chain,state,new Settings{GroundCollision=true},0),"Missing scene handled");
        Assert(state.LastResult == "等待场景碰撞模块", "Zero-query scene failure is exposed");
        var queries = contacts.Queries;
        *(Vector3*)(particles + 96 + 0x40) = new(float.NaN, 0, 0);
        Assert(!contacts.Resolve(chain,state,new Settings{GroundCollision=true},1), "Reject invalid target length before native query");
        Assert(contacts.Queries == queries, "Invalid target length issues no collision calls");
        Assert(state.LastResult == "尾骨段长度尚未有效初始化", "Zero-query length failure is exposed");
        GC.KeepAlive(ray);GC.KeepAlive(sweep);
    }
    static void CompareChains(byte* actual,byte* expected)
    {
        for(int si=0;si<expected[0xD0];si++)
        {
            var sim=*(byte**)(expected+0xD8)+si*104;
            for(int ci=0;ci<sim[2];ci++)
            {
                var c=Chain(expected,si,ci);var n=*(byte**)(c+0x30);var name=NativeMemoryView.Name(n);
                if(name is not ("n_sippo_b" or "j_mimi_l" or "j_mimi_r"))continue;
                byte* match=null;
                for(int ai=0;ai<actual[0xD0];ai++)
                {
                    var aSim=*(byte**)(actual+0xD8)+ai*104;
                    for(int ac=0;ac<aSim[2];ac++)
                    { var a=Chain(actual,ai,ac); if(NativeMemoryView.Name(*(byte**)(a+0x30))==name)match=a; }
                }
                Assert(match!=null,"Expected bone chain "+name);
                Assert(*(ushort*)(c+2)==*(ushort*)(match+2),"Node count "+name);
                // First 36 bytes contain the physical parameters; padding need not be identical.
                for(int f=4;f<36;f+=4)Near(*(float*)(c+f),*(float*)(match+f),"Chain parameter "+name+":"+f);
                var count=*(ushort*)(c+2);var mn=*(byte**)(match+0x30);
                for(int i=0;i<count;i++)
                {
                    Assert(NativeMemoryView.Name(n+i*84)==NativeMemoryView.Name(mn+i*84),"Node name");
                    for(int f=32;f<76;f+=4)Near(*(float*)(n+i*84+f),*(float*)(mn+i*84+f),$"{name} node {i} float {f}");
                    Assert(*(uint*)(n+i*84+76)==*(uint*)(mn+i*84+76),"Collision mask");
                }
            }
        }
    }
}

// Optional LOCAL fixtures: parse a user's extracted PHYB into descriptor views for offline comparison.
// No client assets are committed or required by the normal synthetic test suite.
unsafe sealed class FileFixture : IDisposable
{
    readonly NativeMemoryView mem=new();
    public readonly byte* Handle;
    public FileFixture(byte[] bytes)
    {
        var raw=mem.Allocate(bytes.Length);bytes.CopyTo(new Span<byte>(raw,bytes.Length));
        Handle=mem.Allocate(0x120);*(uint*)(Handle+0xAC)=1;
        var collision=raw+*(uint*)(raw+8);int offset=8;
        int[] sizes=[124,116,92,158,80];
        for(int i=0;i<5;i++){Handle[0xE8+i]=collision[i];*(byte**)(Handle+0xF0+i*8)=collision+offset;offset+=collision[i]*sizes[i];}
        var start=raw+*(uint*)(raw+12);int count=*(int*)start;var basePtr=start+4;
        if(count<1||count>96)throw new InvalidDataException();
        Handle[0xD0]=(byte)count;var sims=mem.Allocate(count*104);*(byte**)(Handle+0xD8)=sims;
        for(int si=0;si<count;si++)
        {
            var from=basePtr+si*72;var sim=sims+si*104;
            new ReadOnlySpan<byte>(from,40).CopyTo(new Span<byte>(sim,40));
            for(int k=0;k<8;k++)*(byte**)(sim+40+k*8)=basePtr+*(uint*)(from+40+k*4);
            if(sim[2]==0)continue;
            var fromChains=*(byte**)(sim+56);var chains=mem.Allocate(sim[2]*56);*(byte**)(sim+56)=chains;
            for(int ci=0;ci<sim[2];ci++)
            {
                var src=fromChains+ci*48;var dest=chains+ci*56;
                new ReadOnlySpan<byte>(src,40).CopyTo(new Span<byte>(dest,40));
                *(byte**)(dest+40)=basePtr+*(uint*)(src+40);
                *(byte**)(dest+48)=basePtr+*(uint*)(src+44);
            }
        }
    }
    public void Dispose()=>mem.Dispose();
}
