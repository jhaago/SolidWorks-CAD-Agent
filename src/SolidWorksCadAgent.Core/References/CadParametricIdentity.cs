using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SolidWorksCadAgent.Core.References
{
    public static class CadParametricIdentity
    {
        public static readonly string[] Roles = { "width", "length", "thickness", "bottom", "right", "top", "left", "centreDiagonal", "boss" };
        public static Guid For(Guid modelId, Guid sketchEntityId, string role)
        {
            if(modelId==Guid.Empty || sketchEntityId==Guid.Empty || !Roles.Contains(role))
                throw new ArgumentException("Trusted model, sketch and supported semantic role are required.");
            using(var stream=new MemoryStream())
            using(var writer=new BinaryWriter(stream,Encoding.UTF8,true))
            using(var sha=SHA256.Create())
            {
                writer.Write(1);writer.Write(modelId.ToByteArray());writer.Write(sketchEntityId.ToByteArray());writer.Write(role);writer.Flush();
                return new Guid(sha.ComputeHash(stream.ToArray()).Take(16).ToArray());
            }
        }
    }
}
