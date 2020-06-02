using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Expertiser
{
    public class DefaultIndex<T>
        where T : class
    {
        public List<string> DeleterRoles { get; }
        public List<string> EditorRoles { get; }
        public DefaultIndex()
        {
        }
        public List<Property> Properties { get; } = new List<Property>();
        public DefaultIndexProperty<T, TProperty> Include<TProperty>(Expression<Func<T, TProperty>> navigationPropertyPath)
        {
            dynamic body = navigationPropertyPath.Body;
            this.Properties.Add(new Property(body.Member));
            this.Last = this.Properties.Last();
            return new DefaultIndexProperty<T, TProperty>(this);
        }
        public DefaultIndexProperty<T, TProperty> Include<TProperty>(Expression<Func<T, IEnumerable<TProperty>>> navigationPropertyPath)
        {
            dynamic body = navigationPropertyPath.Body;
            this.Properties.Add(new Property(body.Member));
            this.Last = this.Properties.Last();
            return new DefaultIndexProperty<T, TProperty>(this);
        }
        internal Property Last { get; set; }
    }
    public class Property
    {
        public PropertyInfo PropertyInfo { get; }
        public List<Property> Properties { get; }
        public Property(PropertyInfo propertyInfo)
        {
            this.PropertyInfo = propertyInfo;
            this.Properties = new List<Property>();
        }
        public IEnumerable<string> Headers()
        {
            if (this.Properties.Count == 0)
                yield return this.PropertyInfo.Name;
            else
                foreach (Property property in this.Properties)
                    foreach (string header in property.Headers())
                        yield return header;
        }
    }
    public class DefaultIndexProperty<T, TPreviousProperty>
        where T : class
    {
        public DefaultIndex<T> DefaultIndex { get; }
        public DefaultIndexProperty(DefaultIndex<T> defaultIndex)
        {
            this.DefaultIndex = defaultIndex;
        }
        public DefaultIndexProperty<T, TProperty> ThenInclude<TProperty>(Expression<Func<TPreviousProperty, TProperty>> navigationPropertyPath)
        {
            dynamic body = navigationPropertyPath.Body;
            this.DefaultIndex.Last.Properties.Add(new Property(body.Member));
            this.DefaultIndex.Last = this.DefaultIndex.Last.Properties.Last();
            return new DefaultIndexProperty<T, TProperty>(this.DefaultIndex);
        }
        public DefaultIndexProperty<T, TProperty> ThenInclude<TProperty>(Expression<Func<TPreviousProperty, IEnumerable<TProperty>>> navigationPropertyPath)
        {
            dynamic body = navigationPropertyPath.Body;
            this.DefaultIndex.Last.Properties.Add(new Property(body.Member));
            this.DefaultIndex.Last = this.DefaultIndex.Last.Properties.Last();
            return new DefaultIndexProperty<T, TProperty>(this.DefaultIndex);
        }
        public DefaultIndexProperty<T, TProperty> Include<TProperty>(Expression<Func<T, TProperty>> navigationPropertyPath)
            => this.DefaultIndex.Include(navigationPropertyPath);
        public DefaultIndexProperty<T, TProperty> Include<TProperty>(Expression<Func<T, IEnumerable<TProperty>>> navigationPropertyPath)
            => this.DefaultIndex.Include(navigationPropertyPath);
        public IEnumerable<string> Headers()
        {
            foreach (var property in this.DefaultIndex.Properties)
                foreach (var t in property.Headers())
                    yield return t;
        }
    }
}
